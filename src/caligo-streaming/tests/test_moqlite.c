#include "check.h"
#include "moqlite.h"
#include "websocket.h"

#include <stdlib.h>

#define PEER_STREAMS 64
#define PEER_BYTES 65536

typedef struct {
    int used;
    uint64_t id;
    unsigned char data[PEER_BYTES];
    size_t length;
    int fin;
    int reset;
    uint64_t reset_code;
    uint64_t reset_size;
    int stopped;
    uint64_t stop_code;
    int more;
} PeerStream;

typedef struct {
    PeerStream streams[PEER_STREAMS];
    int params;
    uint64_t pongs;
    uint64_t pong_value;
    uint64_t pings;
    int closed;
    int broken;
} Peer;

static unsigned char record[QMUX_DEFAULT_RECORD];
static size_t record_length;

static PeerStream *peer_stream(Peer *peer, uint64_t id) {
    for (int index = 0; index < PEER_STREAMS; index++) {
        if (peer->streams[index].used && peer->streams[index].id == id) {
            return &peer->streams[index];
        }
    }

    for (int index = 0; index < PEER_STREAMS; index++) {
        if (!peer->streams[index].used) {
            memset(&peer->streams[index], 0, sizeof(PeerStream));

            peer->streams[index].used = 1;
            peer->streams[index].id = id;

            return &peer->streams[index];
        }
    }

    return NULL;
}

static PeerStream *find(Peer *peer, uint64_t id) {
    for (int index = 0; index < PEER_STREAMS; index++) {
        if (peer->streams[index].used && peer->streams[index].id == id) {
            return &peer->streams[index];
        }
    }

    return NULL;
}

static void drain(Peer *peer, MoqlSession *session) {
    size_t position = session->out_offset;

    while (position + 2 <= session->out_length) {
        unsigned char *message = session->out + position;
        uint64_t length = message[1] & 0x7F;
        size_t header = 2;

        if (length == 126) {
            length = (uint64_t)message[2] << 8 | message[3];
            header = 4;
        }

        if ((message[0] & 0x0F) != WS_BINARY) {
            peer->broken = 1;

            return;
        }

        const unsigned char *frames = message + header;
        size_t offset = 0;

        while (offset < length) {
            QmuxFrame frame;
            long used = qmux_decode(frames + offset, (size_t)length - offset, &frame);

            if (used <= 0) {
                peer->broken = 1;

                return;
            }

            offset += (size_t)used;

            if (frame.type == QMUX_FRAME_PARAMETERS) {
                peer->params++;
            }
            else if (frame.type == QMUX_FRAME_STREAM) {
                PeerStream *stream = peer_stream(peer, frame.stream);

                if (!stream || frame.offset != stream->length || stream->length + frame.length > PEER_BYTES) {
                    peer->broken = 1;

                    return;
                }

                memcpy(stream->data + stream->length, frame.data, frame.length);

                stream->length += frame.length;
                stream->fin |= frame.fin;
            }
            else if (frame.type == QMUX_FRAME_RESET_STREAM) {
                PeerStream *stream = peer_stream(peer, frame.stream);

                stream->reset = 1;
                stream->reset_code = frame.value;
                stream->reset_size = frame.size;
            }
            else if (frame.type == QMUX_FRAME_STOP_SENDING) {
                peer_stream(peer, frame.stream)->stopped = 1;
            }
            else if (frame.type == QMUX_FRAME_PONG) {
                peer->pongs++;
                peer->pong_value = frame.value;
            }
            else if (frame.type == QMUX_FRAME_PING) {
                peer->pings++;
            }
            else if (frame.type == QMUX_FRAME_CONNECTION_CLOSE) {
                peer->closed = 1;
            }
        }

        position += header + (size_t)length;
    }

    moql_consumed(session, session->out_length - session->out_offset);
}

static void begin(void) {
    record_length = 0;
}

static void add(const unsigned char *bytes, size_t length) {
    memcpy(record + record_length, bytes, length);

    record_length += length;
}

static void add_stream(uint64_t id, uint64_t offset, const unsigned char *data, size_t length, int fin) {
    unsigned char header[32];

    add(header, qmux_stream_header(header, id, offset, length, fin));
    add(data, length);
}

static int send_record(MoqlSession *session) {
    return moql_receive(session, record, record_length, 0);
}

static void client_params(MoqlSession *session, uint64_t max_data, uint64_t max_uni) {
    QmuxParams params;
    unsigned char frame[160];

    memset(&params, 0, sizeof(params));

    params.max_idle_timeout = 30000;
    params.initial_max_data = max_data;
    params.initial_max_stream_data_bidi_local = 262144;
    params.initial_max_stream_data_bidi_remote = 262144;
    params.initial_max_stream_data_uni = 262144;
    params.initial_max_streams_bidi = 100;
    params.initial_max_streams_uni = max_uni;
    params.max_record_size = QMUX_DEFAULT_RECORD;

    begin();
    add(frame, qmux_params_frame(frame, sizeof(frame), &params));

    CHECK_EQUAL(0, send_record(session));
}

static size_t put_varint(unsigned char *out, uint64_t value) {
    return varint_encode(value, out);
}

static size_t put_string(unsigned char *out, const char *text) {
    size_t length = strlen(text);
    size_t position = put_varint(out, length);

    memcpy(out + position, text, length);

    return position + length;
}

static size_t message(unsigned char *out, const unsigned char *body, size_t length) {
    size_t position = put_varint(out, length);

    memcpy(out + position, body, length);

    return position + length;
}

static size_t subscribe_bytes(unsigned char *out, uint64_t id, const char *track) {
    unsigned char body[256];
    size_t length = 0;
    size_t position = put_varint(out, 2);

    length += put_varint(body + length, id);
    length += put_string(body + length, "caligo/radio/main");
    length += put_string(body + length, track);
    body[length++] = 0;

    for (int field = 0; field < 5; field++) {
        length += put_varint(body + length, 0);
    }

    return position + message(out + position, body, length);
}

static void append_audio(ObjectTrack *track, int first, int count) {
    for (int index = first; index < first + count; index++) {
        unsigned char payload[64];
        size_t length = 0;

        payload[length++] = 0;
        length += varint_encode((uint64_t)index * 960, payload + length);
        payload[length++] = 0xF8;
        payload[length++] = (unsigned char)(index >> 8);
        payload[length++] = (unsigned char)index;
        memset(payload + length, 0x11, 20);
        length += 20;

        object_track_append(track, payload, length, (uint64_t)index * 960, 0);
    }
}

typedef struct {
    uint64_t subscribe;
    uint64_t sequence;
    uint64_t frame_start;
    int frames;
    uint64_t timestamps[64];
    int counters[64];
} GroupView;

static int read_group(const PeerStream *stream, GroupView *view) {
    const unsigned char *data = stream->data;
    size_t length = stream->length;
    size_t position = 0;
    uint64_t value = 0;
    uint64_t size = 0;
    uint64_t timestamp = 0;

    memset(view, 0, sizeof(*view));

    position += (size_t)varint_decode(data, length, &value);

    if (value != 0) {
        return 0;
    }

    position += (size_t)varint_decode(data + position, length - position, &size);

    size_t end = position + (size_t)size;

    position += (size_t)varint_decode(data + position, length - position, &view->subscribe);
    position += (size_t)varint_decode(data + position, length - position, &view->sequence);
    position += (size_t)varint_decode(data + position, length - position, &view->frame_start);

    if (position != end) {
        return 0;
    }

    while (position < length && view->frames < 64) {
        uint64_t delta = 0;

        position += (size_t)varint_decode(data + position, length - position, &delta);
        position += (size_t)varint_decode(data + position, length - position, &size);

        timestamp += delta >> 1;
        view->timestamps[view->frames] = timestamp;
        view->counters[view->frames] = data[position + 1] << 8 | data[position + 2];
        view->frames++;
        position += (size_t)size;
    }

    return position == length;
}

static MoqlSource source;
static ObjectTrack audio;
static ObjectTrack meta;
static const unsigned char catalog[] = "{\"version\":0}";

static void reset_source(void) {
    object_track_free(&audio);
    object_track_free(&meta);
    object_track_init(&audio, 1 << 20, 4096, 100, 48000);
    object_track_init(&meta, 4096, 16, 100, 0);

    source.broadcast = "caligo/radio/main";
    source.audio = &audio;
    source.meta = &meta;
    source.catalog = catalog;
    source.catalog_length = sizeof(catalog) - 1;
    source.catalog_version = 1;
    source.live = 1;
}

static void parameters_come_first(void) {
    unsigned char data[4] = { 1, 1, 0, 0 };

    reset_source();

    MoqlSession *session = moql_create(&source, 2, 77, 0);

    begin();
    add_stream(2, 0, data, sizeof(data), 0);

    CHECK_EQUAL(-1, send_record(session));
    CHECK_TEXT("transport parameters must come first", session->error);

    moql_free(session);
}

static void tracks_are_described_and_groups_streamed(void) {
    static Peer peer;
    unsigned char bytes[512];
    unsigned char body[256];
    size_t length;
    size_t body_length = 0;
    GroupView view;

    reset_source();
    memset(&peer, 0, sizeof(peer));

    MoqlSession *session = moql_create(&source, 2, 77, 0);

    moql_pump(session, 0);
    drain(&peer, session);

    CHECK_EQUAL(1, peer.params);
    CHECK(find(&peer, 3) == NULL);

    client_params(session, 1 << 20, 2);
    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *setup = find(&peer, 3);

    CHECK(setup && setup->fin && setup->length == 3);
    CHECK(setup && setup->data[0] == 1 && setup->data[1] == 1 && setup->data[2] == 0);

    length = put_varint(bytes, 6);
    body_length = put_string(body, "caligo/radio/main");
    body_length += put_string(body + body_length, "audio/hi");
    length += message(bytes + length, body, body_length);

    begin();
    add_stream(0, 0, bytes, length, 1);

    CHECK_EQUAL(0, send_record(session));

    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *info = find(&peer, 0);
    uint64_t value = 0;

    CHECK(info && info->fin);
    CHECK(info && info->data[1] == 0);
    CHECK(info && varint_decode(info->data + 2, info->length - 2, &value) > 0 && value == MOQL_AUDIO_MAX_AGE_MS);
    CHECK(info && varint_decode(info->data + info->length - 4, 4, &value) == 4 && value == 48000);

    begin();
    add_stream(4, 0, bytes, subscribe_bytes(bytes, 9, "audio/hi"), 0);

    CHECK_EQUAL(0, send_record(session));

    append_audio(&audio, 0, 60);
    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *control = find(&peer, 4);
    PeerStream *first = find(&peer, 7);

    CHECK(control && control->length == 4 && control->data[0] == 0 && control->data[1] == 2);
    CHECK(control && control->data[2] == 0x40 && control->data[3] == 100);
    CHECK(first && first->fin && read_group(first, &view));
    CHECK_EQUAL(9, view.subscribe);
    CHECK_EQUAL(100, view.sequence);
    CHECK_EQUAL(0, view.frame_start);
    CHECK_EQUAL(50, view.frames);
    CHECK_EQUAL(0, view.timestamps[0]);
    CHECK_EQUAL(49 * 960, view.timestamps[49]);
    CHECK_EQUAL(49, view.counters[49]);
    CHECK(find(&peer, 11) == NULL);

    unsigned char frame[16];

    begin();
    add(frame, qmux_max_streams(frame, 0, 10));

    CHECK_EQUAL(0, send_record(session));

    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *second = find(&peer, 11);

    CHECK(second && !second->fin && read_group(second, &view));
    CHECK_EQUAL(101, view.sequence);
    CHECK_EQUAL(10, view.frames);
    CHECK_EQUAL(50 * 960, view.timestamps[0]);
    CHECK(!peer.broken);

    moql_free(session);
}

static void a_starved_subscriber_skips_to_the_newest_group(void) {
    static Peer peer;
    unsigned char bytes[256];
    GroupView view;

    reset_source();
    memset(&peer, 0, sizeof(peer));

    MoqlSession *session = moql_create(&source, 2, 77, 0);

    client_params(session, 1500, 100);

    begin();
    add_stream(0, 0, bytes, subscribe_bytes(bytes, 3, "audio/hi"), 0);

    CHECK_EQUAL(0, send_record(session));

    append_audio(&audio, 0, 30);
    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *stale = find(&peer, 7);

    CHECK(stale && !stale->fin && stale->length > 0);

    append_audio(&audio, 30, 120);
    moql_pump(session, 0);
    drain(&peer, session);

    CHECK(stale && stale->reset);
    CHECK_EQUAL(MOQL_ERROR_OLD, stale->reset_code);
    CHECK(stale && stale->reset_size == stale->length);
    CHECK_EQUAL(1, session->skips);

    unsigned char frame[16];

    begin();
    add(frame, qmux_max_data(frame, 1 << 24));

    CHECK_EQUAL(0, send_record(session));

    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *latest = find(&peer, 11);

    CHECK(latest && read_group(latest, &view));
    CHECK_EQUAL(102, view.sequence);
    CHECK_EQUAL(0, view.frame_start);
    CHECK_EQUAL(50, view.frames);
    CHECK_EQUAL(100, view.counters[0]);
    CHECK(!peer.broken);

    moql_free(session);
}

static void announce_lists_the_broadcast_under_a_prefix(void) {
    static Peer peer;
    unsigned char bytes[128];
    unsigned char body[64];
    size_t length;
    uint64_t hop = 0;
    uint64_t count = 0;
    char suffix[64];

    reset_source();
    memset(&peer, 0, sizeof(peer));

    MoqlSession *session = moql_create(&source, 2, 77, 0);

    client_params(session, 1 << 20, 100);

    length = put_varint(bytes, 1);
    length += message(bytes + length, body, put_string(body, "caligo/"));

    begin();
    add_stream(0, 0, bytes, length, 0);

    length = put_varint(bytes, 1);
    length += message(bytes + length, body, put_string(body, "elsewhere"));
    add_stream(4, 0, bytes, length, 0);

    CHECK_EQUAL(0, send_record(session));

    moql_pump(session, 0);
    drain(&peer, session);

    PeerStream *matched = find(&peer, 0);
    PeerStream *other = find(&peer, 4);

    CHECK(matched != NULL && other != NULL);

    if (matched && other) {
        size_t position = 1;

        position += (size_t)varint_decode(matched->data + position, matched->length - position, &hop);
        position += (size_t)varint_decode(matched->data + position, matched->length - position, &count);

        CHECK_EQUAL(77, hop);
        CHECK_EQUAL(1, count);
        CHECK_EQUAL(0, matched->data[position]);

        size_t name = matched->data[position + 2];

        memcpy(suffix, matched->data + position + 3, name);
        suffix[name] = '\0';

        CHECK_TEXT("radio/main", suffix);
        CHECK_EQUAL(4, other->length);
        CHECK_EQUAL(0, other->data[3]);
    }

    moql_free(session);
}

static void pings_and_bad_peers(void) {
    static Peer peer;
    unsigned char frame[32];
    unsigned char bytes[256];

    reset_source();
    memset(&peer, 0, sizeof(peer));

    MoqlSession *session = moql_create(&source, 2, 77, 0);

    client_params(session, 1 << 20, 100);

    begin();
    add(frame, qmux_ping(frame, 0, 5));

    CHECK_EQUAL(0, send_record(session));

    begin();
    add_stream(0, 0, bytes, subscribe_bytes(bytes, 1, "no-such-track"), 0);

    CHECK_EQUAL(0, send_record(session));

    moql_pump(session, 0);
    drain(&peer, session);

    CHECK_EQUAL(1, peer.pongs);
    CHECK_EQUAL(5, peer.pong_value);

    PeerStream *refused = find(&peer, 0);

    CHECK(refused && refused->reset && refused->stopped);
    CHECK(refused && refused->reset_code == MOQL_ERROR_NOT_FOUND);

    moql_pump(session, MOQL_PING_MS + 1);
    drain(&peer, session);

    CHECK_EQUAL(1, peer.pings);

    begin();
    add(frame, qmux_ping(frame, 1, 9));

    CHECK_EQUAL(-1, send_record(session));
    CHECK_TEXT("a pong for a ping never sent", session->error);

    moql_free(session);

    session = moql_create(&source, 2, 77, 0);
    client_params(session, 1 << 20, 100);

    begin();
    add_stream(3, 0, bytes, 4, 0);

    CHECK_EQUAL(-1, send_record(session));

    moql_free(session);

    session = moql_create(&source, 2, 77, 0);
    client_params(session, 1 << 20, 100);

    begin();
    add_stream(0, 5, bytes, 4, 0);

    CHECK_EQUAL(-1, send_record(session));
    CHECK_TEXT("stream data out of order", session->error);

    moql_close(session, 0, "bye");
    drain(&peer, session);

    CHECK_EQUAL(1, peer.closed);

    moql_free(session);
}

typedef struct {
    Peer peer;
    uint64_t next_uni;
    uint64_t active[PEER_STREAMS];
    int active_count;
    int wakes;
    int resets;
    int fail_open;
} FakeQuic;

static uint64_t fake_open_uni(void *context) {
    FakeQuic *quic = context;

    if (quic->fail_open) {
        return MOQL_NO_STREAM;
    }

    uint64_t id = quic->next_uni;

    quic->next_uni += 4;

    return id;
}

static void fake_wake(void *context, uint64_t stream) {
    FakeQuic *quic = context;

    quic->wakes++;

    for (int index = 0; index < quic->active_count; index++) {
        if (quic->active[index] == stream) {
            return;
        }
    }

    if (quic->active_count < PEER_STREAMS) {
        quic->active[quic->active_count++] = stream;
    }
}

static void fake_reset(void *context, uint64_t stream, uint64_t code) {
    FakeQuic *quic = context;
    PeerStream *target = peer_stream(&quic->peer, stream);

    quic->resets++;

    if (target) {
        target->reset = 1;
        target->reset_code = code;
        target->reset_size = target->length;
    }
}

static void fake_stop(void *context, uint64_t stream, uint64_t code) {
    FakeQuic *quic = context;
    PeerStream *target = peer_stream(&quic->peer, stream);

    if (target) {
        target->stopped = 1;
        target->stop_code = code;
    }
}

static unsigned char *fake_buffer(void *context, size_t length, int fin, int more) {
    PeerStream *stream = context;

    if (stream->length + length > PEER_BYTES) {
        return NULL;
    }

    stream->fin |= fin;
    stream->more = more;
    stream->length += length;

    return stream->data + stream->length - length;
}

static int pull(FakeQuic *quic, MoqlSession *session, size_t space, int budget) {
    int calls = 0;

    while (quic->active_count > 0 && calls < budget) {
        int index = 0;

        while (index < quic->active_count && calls < budget) {
            uint64_t id = quic->active[index];
            PeerStream *stream = peer_stream(&quic->peer, id);

            stream->more = 0;

            if (moql_stream_send(session, id, space, fake_buffer, stream) < 0) {
                quic->peer.broken = 1;
            }

            calls++;

            if (stream->more) {
                index++;
            }
            else {
                quic->active[index] = quic->active[--quic->active_count];
            }
        }
    }

    return calls;
}

static MoqlSession *native_session(FakeQuic *quic, MoqlTransport *transport) {
    memset(quic, 0, sizeof(*quic));

    quic->next_uni = 15;
    transport->context = quic;
    transport->open_uni = fake_open_uni;
    transport->wake = fake_wake;
    transport->reset = fake_reset;
    transport->stop = fake_stop;

    return moql_create_native(&source, 2, 77, transport, 0);
}

static void native_sessions_use_quic_streams(void) {
    static FakeQuic quic;
    MoqlTransport transport;
    unsigned char bytes[512];
    unsigned char body[256];
    size_t length;
    size_t body_length;
    GroupView view;

    reset_source();

    MoqlSession *session = native_session(&quic, &transport);

    moql_pump(session, 0);
    pull(&quic, session, 1200, 100);

    PeerStream *setup = find(&quic.peer, 15);

    CHECK(setup && setup->fin && setup->length == 3);
    CHECK(setup && setup->data[0] == 1 && setup->data[1] == 1 && setup->data[2] == 0);

    length = put_varint(bytes, 6);
    body_length = put_string(body, "caligo/radio/main");
    body_length += put_string(body + body_length, "audio/hi");
    length += message(bytes + length, body, body_length);

    CHECK_EQUAL(0, moql_stream_data(session, 4, bytes, length, 1));

    length = subscribe_bytes(bytes, 9, "audio/hi");

    CHECK_EQUAL(0, moql_stream_data(session, 8, bytes, 3, 0));
    CHECK_EQUAL(0, moql_stream_data(session, 8, bytes + 3, length - 3, 0));

    append_audio(&audio, 0, 60);
    moql_pump(session, 0);
    pull(&quic, session, 1200, 1000);

    PeerStream *info = find(&quic.peer, 4);
    PeerStream *control = find(&quic.peer, 8);
    PeerStream *first = find(&quic.peer, 19);
    PeerStream *second = find(&quic.peer, 23);
    uint64_t value = 0;

    CHECK(info && info->fin && info->data[1] == 0);
    CHECK(info && varint_decode(info->data + info->length - 4, 4, &value) == 4 && value == 48000);
    CHECK(control && !control->fin && control->length == 4);
    CHECK(control && control->data[0] == 0 && control->data[2] == 0x40 && control->data[3] == 100);
    CHECK(first && first->fin && read_group(first, &view));
    CHECK_EQUAL(9, view.subscribe);
    CHECK_EQUAL(100, view.sequence);
    CHECK_EQUAL(50, view.frames);
    CHECK_EQUAL(49 * 960, view.timestamps[49]);
    CHECK_EQUAL(49, view.counters[49]);
    CHECK(second && !second->fin && read_group(second, &view));
    CHECK_EQUAL(101, view.sequence);
    CHECK_EQUAL(10, view.frames);

    int wakes = quic.wakes;

    moql_pump(session, 0);

    CHECK_EQUAL(wakes, quic.wakes);

    append_audio(&audio, 60, 5);
    moql_pump(session, 0);
    pull(&quic, session, 1200, 100);

    CHECK(second && read_group(second, &view) && view.frames == 15);
    CHECK_EQUAL(64, view.counters[14]);

    CHECK_EQUAL(0, moql_stream_data(session, 8, NULL, 0, 1));

    moql_pump(session, 0);
    pull(&quic, session, 1200, 100);

    CHECK(second && second->reset && second->reset_code == MOQL_ERROR_CANCELLED);
    CHECK(control && control->fin);
    CHECK_EQUAL(0, session->subscriptions[0].used);
    CHECK(session->error == NULL);
    CHECK(!quic.peer.broken);

    moql_free(session);
}

static void native_backpressure_skips_to_the_newest_group(void) {
    static FakeQuic quic;
    MoqlTransport transport;
    unsigned char bytes[256];
    GroupView view;

    reset_source();

    MoqlSession *session = native_session(&quic, &transport);

    CHECK_EQUAL(0, moql_stream_data(session, 0, bytes, subscribe_bytes(bytes, 3, "audio/hi"), 0));

    append_audio(&audio, 0, 30);
    moql_pump(session, 0);
    pull(&quic, session, 100, 4);

    PeerStream *stale = find(&quic.peer, 19);

    CHECK(stale && !stale->fin && stale->length == 200);

    append_audio(&audio, 30, 120);
    moql_pump(session, 0);

    CHECK(stale && stale->reset);
    CHECK_EQUAL(MOQL_ERROR_OLD, stale->reset_code);
    CHECK_EQUAL(1, session->skips);

    pull(&quic, session, 1200, 1000);

    PeerStream *latest = find(&quic.peer, 23);

    CHECK(stale && stale->length == 200);
    CHECK(latest && read_group(latest, &view));
    CHECK_EQUAL(102, view.sequence);
    CHECK_EQUAL(0, view.frame_start);
    CHECK_EQUAL(50, view.frames);
    CHECK_EQUAL(100, view.counters[0]);
    CHECK(!quic.peer.broken);

    moql_free(session);
}

static void native_stop_sending_moves_to_the_next_group(void) {
    static FakeQuic quic;
    MoqlTransport transport;
    unsigned char bytes[256];
    GroupView view;

    reset_source();

    MoqlSession *session = native_session(&quic, &transport);

    CHECK_EQUAL(0, moql_stream_data(session, 0, bytes, subscribe_bytes(bytes, 5, "audio/hi"), 0));

    append_audio(&audio, 0, 60);
    moql_pump(session, 0);
    pull(&quic, session, 1200, 1000);

    PeerStream *cancelled = find(&quic.peer, 23);

    CHECK(cancelled && !cancelled->fin && cancelled->length > 0);

    moql_stream_stop(session, 23, 0x10);
    moql_pump(session, 0);

    CHECK(cancelled && cancelled->reset && cancelled->reset_code == 0x10);

    append_audio(&audio, 60, 50);
    moql_pump(session, 0);
    pull(&quic, session, 1200, 1000);

    PeerStream *next = find(&quic.peer, 27);

    CHECK(next && read_group(next, &view));
    CHECK_EQUAL(102, view.sequence);
    CHECK_EQUAL(10, view.frames);
    CHECK_EQUAL(100, view.counters[0]);
    CHECK(!quic.peer.broken);

    moql_free(session);
}

static void native_refusals_and_late_data(void) {
    static FakeQuic quic;
    MoqlTransport transport;
    unsigned char bytes[256];

    reset_source();

    MoqlSession *session = native_session(&quic, &transport);

    CHECK_EQUAL(0, moql_stream_data(session, 0, bytes, subscribe_bytes(bytes, 1, "no-such-track"), 0));

    moql_pump(session, 0);
    pull(&quic, session, 1200, 100);

    PeerStream *refused = find(&quic.peer, 0);

    CHECK(refused && refused->stopped && refused->stop_code == MOQL_ERROR_NOT_FOUND);
    CHECK(refused && refused->reset && refused->reset_code == MOQL_ERROR_NOT_FOUND);

    int used = 0;

    for (int index = 0; index < MOQL_STREAMS; index++) {
        used += session->streams[index].used;
    }

    CHECK_EQUAL(0, used);
    CHECK_EQUAL(0, moql_stream_data(session, 0, bytes, 4, 0));

    for (int index = 0; index < MOQL_STREAMS; index++) {
        CHECK(!session->streams[index].used);
    }

    quic.fail_open = 1;

    CHECK_EQUAL(0, moql_stream_data(session, 4, bytes, subscribe_bytes(bytes, 2, "audio/hi"), 0));

    append_audio(&audio, 0, 10);
    moql_pump(session, 0);

    CHECK(find(&quic.peer, 19) == NULL);

    quic.fail_open = 0;

    moql_pump(session, 0);
    pull(&quic, session, 1200, 100);

    PeerStream *late = find(&quic.peer, 19);
    GroupView view;

    CHECK(late && read_group(late, &view) && view.frames == 10);
    CHECK_EQUAL(-1, moql_stream_data(session, 19, bytes, 4, 0));
    CHECK_TEXT("data on a stream the relay opened", session->error);

    moql_free(session);
}

static void codec_round_trips(void) {
    unsigned char out[128];
    QmuxFrame frame;
    QmuxParams params;
    size_t length;

    length = qmux_reset_stream(out, 7, 0x34, 900);

    CHECK_EQUAL((long)length, qmux_decode(out, length, &frame));
    CHECK(frame.type == QMUX_FRAME_RESET_STREAM && frame.stream == 7 && frame.value == 0x34 && frame.size == 900);

    length = qmux_max_stream_data(out, 4, 70000);

    CHECK_EQUAL((long)length, qmux_decode(out, length, &frame));
    CHECK(frame.type == QMUX_FRAME_MAX_STREAM_DATA && frame.stream == 4 && frame.value == 70000);

    length = qmux_close(out, sizeof(out), 3, "nope");

    CHECK_EQUAL((long)length, qmux_decode(out, length, &frame));
    CHECK(frame.type == QMUX_FRAME_CONNECTION_CLOSE && frame.value == 3 && frame.length == 4);

    const unsigned char ping_frame[] = { 0x01 };
    const unsigned char forbidden[] = { 0x01, 0x02, 0x00, 0x00 };
    const unsigned char short_record[] = { 0x01, 0x04, 0x80 };

    CHECK_EQUAL(-1, qmux_decode(ping_frame, sizeof(ping_frame), &frame));
    CHECK_EQUAL(-1, qmux_params_parse(forbidden + 1, 3, &params));
    CHECK_EQUAL(0, qmux_params_parse(forbidden, 0, &params));
    CHECK_EQUAL(QMUX_DEFAULT_RECORD, params.max_record_size);
    CHECK_EQUAL(-1, qmux_params_parse(short_record, sizeof(short_record), &params));
}

int main(void) {
    codec_round_trips();
    parameters_come_first();
    tracks_are_described_and_groups_streamed();
    a_starved_subscriber_skips_to_the_newest_group();
    announce_lists_the_broadcast_under_a_prefix();
    pings_and_bad_peers();
    native_sessions_use_quic_streams();
    native_backpressure_skips_to_the_newest_group();
    native_stop_sending_moves_to_the_next_group();
    native_refusals_and_late_data();

    object_track_free(&audio);
    object_track_free(&meta);

    CHECK_REPORT("moqlite");
}
