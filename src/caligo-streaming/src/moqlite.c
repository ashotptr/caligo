#include "moqlite.h"

#include <stdlib.h>
#include <string.h>

#include "websocket.h"

#define RECV_STREAM_WINDOW 65536
#define RECV_DATA_WINDOW (1024 * 1024)
#define BIDI_GRANT 32
#define UNI_GRANT 8
#define UNLIMITED (UINT64_MAX / 4)
#define BUILD_MAX (MOQL_PENDING_MAX + 64)
#define SCRATCH_MAX (65536 + 64)
#define TEXT_MAX 256

typedef struct {
    const unsigned char *data;
    size_t length;
    size_t position;
    int ok;
} Cursor;

typedef struct {
    unsigned char data[BUILD_MAX];
    size_t length;
    int ok;
} Builder;

static unsigned char scratch[SCRATCH_MAX];

static uint64_t cursor_varint(Cursor *cursor) {
    uint64_t value = 0;
    long used = cursor->ok ? varint_decode(cursor->data + cursor->position, cursor->length - cursor->position, &value) : 0;

    if (used <= 0) {
        cursor->ok = 0;

        return 0;
    }

    cursor->position += (size_t)used;

    return value;
}

static int cursor_u8(Cursor *cursor) {
    if (!cursor->ok || cursor->position >= cursor->length) {
        cursor->ok = 0;

        return 0;
    }

    return cursor->data[cursor->position++];
}

static void cursor_string(Cursor *cursor, char *out, size_t capacity) {
    uint64_t length = cursor_varint(cursor);

    out[0] = '\0';

    if (!cursor->ok || length > cursor->length - cursor->position || length >= capacity ||
        memchr(cursor->data + cursor->position, '\0', (size_t)length) != NULL) {
        cursor->ok = 0;

        return;
    }

    memcpy(out, cursor->data + cursor->position, (size_t)length);

    out[length] = '\0';
    cursor->position += (size_t)length;
}

static void put_bytes(Builder *builder, const void *bytes, size_t length) {
    if (!builder->ok || length > sizeof(builder->data) - builder->length) {
        builder->ok = 0;

        return;
    }

    memcpy(builder->data + builder->length, bytes, length);

    builder->length += length;
}

static void put_varint(Builder *builder, uint64_t value) {
    unsigned char bytes[8];

    put_bytes(builder, bytes, varint_encode(value, bytes));
}

static void put_string(Builder *builder, const char *text) {
    size_t length = strlen(text);

    put_varint(builder, length);
    put_bytes(builder, text, length);
}

static void put_message(Builder *builder, const Builder *body) {
    if (!body->ok) {
        builder->ok = 0;

        return;
    }

    put_varint(builder, body->length);
    put_bytes(builder, body->data, body->length);
}

static void builder_init(Builder *builder) {
    builder->length = 0;
    builder->ok = 1;
}

static long message_body(const unsigned char *data, size_t length, const unsigned char **body, size_t *body_length) {
    uint64_t size = 0;
    long used = varint_decode(data, length, &size);

    if (used <= 0) {
        return 0;
    }

    if (size > MOQL_IN_MAX) {
        return -1;
    }

    if (size > length - (size_t)used) {
        return 0;
    }

    *body = data + used;
    *body_length = (size_t)size;

    return used + (long)size;
}

static int client_stream(uint64_t id) {
    return (id & 1) == 0;
}

static int bidi_stream(uint64_t id) {
    return (id & 2) == 0;
}

static int queue_control(MoqlSession *session, const unsigned char *frame, size_t length) {
    if (session->transport) {
        return 1;
    }

    if (length > sizeof(session->control) - session->control_length) {
        return 0;
    }

    memcpy(session->control + session->control_length, frame, length);

    session->control_length += length;

    return 1;
}

static int stream_find(const MoqlSession *session, uint64_t id) {
    for (int index = 0; index < MOQL_STREAMS; index++) {
        if (session->streams[index].used && session->streams[index].id == id) {
            return index;
        }
    }

    return -1;
}

static int stream_add(MoqlSession *session, uint64_t id) {
    for (int index = 0; index < MOQL_STREAMS; index++) {
        MoqlStream *stream = &session->streams[index];

        if (!stream->used) {
            memset(stream, 0, sizeof(*stream));

            stream->used = 1;
            stream->id = id;
            stream->kind = -1;
            stream->subscription = -1;
            stream->recv_window = session->transport ? UNLIMITED : RECV_STREAM_WINDOW;
            stream->send_limit = !client_stream(id) ? session->peer.initial_max_stream_data_uni
                : bidi_stream(id) ? session->peer.initial_max_stream_data_bidi_local : 0;
            stream->recv_done = !client_stream(id);
            stream->send_done = client_stream(id) && !bidi_stream(id);

            return index;
        }
    }

    return -1;
}

static int open_uni(MoqlSession *session) {
    int free_slot = 0;

    for (int index = 0; index < MOQL_STREAMS && !free_slot; index++) {
        free_slot = !session->streams[index].used;
    }

    if (!free_slot || session->opened_uni >= session->peer.initial_max_streams_uni) {
        return -1;
    }

    uint64_t id = session->transport ? session->transport->open_uni(session->transport->context) : 4 * session->opened_uni + 3;

    if (id == MOQL_NO_STREAM) {
        return -1;
    }

    int index = stream_add(session, id);

    if (index >= 0) {
        session->opened_uni++;
    }

    return index;
}

static void count_closed(MoqlSession *session, uint64_t id) {
    unsigned char frame[16];

    if (bidi_stream(id)) {
        session->peer_bidi_closed++;

        if (session->max_bidi_granted - session->peer_bidi_closed < BIDI_GRANT / 2) {
            session->max_bidi_granted = session->peer_bidi_closed + BIDI_GRANT;

            queue_control(session, frame, qmux_max_streams(frame, 1, session->max_bidi_granted));
        }
    }
    else {
        session->peer_uni_closed++;

        if (session->max_uni_granted - session->peer_uni_closed < UNI_GRANT / 2) {
            session->max_uni_granted = session->peer_uni_closed + UNI_GRANT;

            queue_control(session, frame, qmux_max_streams(frame, 0, session->max_uni_granted));
        }
    }
}

static void maybe_release(MoqlSession *session, int index) {
    MoqlStream *stream = &session->streams[index];

    if (!stream->used || !stream->recv_done || !stream->send_done || stream->pending_length > 0 ||
        stream->reset_pending || stream->stop_pending) {
        return;
    }

    uint64_t id = stream->id;

    if (stream->subscription >= 0 && session->subscriptions[stream->subscription].used &&
        session->subscriptions[stream->subscription].group_stream == index) {
        session->subscriptions[stream->subscription].group_stream = -1;
    }

    free(stream->in);
    free(stream->pending);
    memset(stream, 0, sizeof(*stream));

    if (client_stream(id)) {
        session->closed_ids[session->closed_next++ % MOQL_STREAMS] = id + 1;

        count_closed(session, id);
    }
}

static int recently_closed(const MoqlSession *session, uint64_t id) {
    for (int index = 0; index < MOQL_STREAMS; index++) {
        if (session->closed_ids[index] == id + 1) {
            return 1;
        }
    }

    return 0;
}

static int stream_queue(MoqlSession *session, int index, const Builder *builder) {
    MoqlStream *stream = &session->streams[index];

    if (!builder->ok || stream->send_done || stream->fin_queued || stream->reset_pending) {
        return -1;
    }

    if (builder->length > MOQL_PENDING_MAX - stream->pending_length) {
        return 0;
    }

    if (!stream->pending) {
        stream->pending = malloc(MOQL_PENDING_MAX);

        if (!stream->pending) {
            return -1;
        }
    }

    memcpy(stream->pending + stream->pending_length, builder->data, builder->length);

    stream->pending_length += builder->length;

    return 1;
}

static void stream_finish(MoqlSession *session, int index) {
    MoqlStream *stream = &session->streams[index];

    if (!stream->send_done && !stream->reset_pending) {
        stream->fin_queued = 1;
    }

    (void)session;
}

static void stream_reset(MoqlSession *session, int index, uint64_t code) {
    MoqlStream *stream = &session->streams[index];

    if (!stream->send_done && !stream->reset_pending) {
        stream->reset_pending = 1;
        stream->reset_code = code;
        stream->pending_length = 0;
        stream->fin_queued = 0;
    }

    (void)session;
}

static void stream_refuse(MoqlSession *session, int index, uint64_t code) {
    MoqlStream *stream = &session->streams[index];

    if (!stream->recv_done) {
        stream->stop_pending = 1;
        stream->stop_code = code;
        stream->recv_done = 1;
        stream->in_length = 0;
    }

    stream_reset(session, index, code);
}

static int track_named(const MoqlSession *session, const char *broadcast, const char *track) {
    if (strcmp(broadcast, session->source->broadcast) != 0) {
        return -1;
    }

    if (strcmp(track, "audio/hi") == 0) {
        return MOQL_TRACK_AUDIO;
    }

    if (strcmp(track, "meta") == 0) {
        return MOQL_TRACK_META;
    }

    return strcmp(track, "catalog") == 0 ? MOQL_TRACK_CATALOG : -1;
}

static const ObjectTrack *object_track_of(const MoqlSession *session, int track) {
    return track == MOQL_TRACK_AUDIO ? session->source->audio : track == MOQL_TRACK_META ? session->source->meta : NULL;
}

static void trim_path(char *path) {
    size_t length = strlen(path);
    size_t start = 0;

    while (start < length && path[start] == '/') {
        start++;
    }

    while (length > start && path[length - 1] == '/') {
        length--;
    }

    memmove(path, path + start, length - start);

    path[length - start] = '\0';
}

static const char *suffix_under(const char *path, const char *prefix) {
    size_t length = strlen(prefix);

    if (length == 0) {
        return path;
    }

    if (strncmp(path, prefix, length) != 0) {
        return NULL;
    }

    if (path[length] == '\0') {
        return path + length;
    }

    return path[length] == '/' ? path + length + 1 : NULL;
}

static void handle_announce(MoqlSession *session, int index, const unsigned char *body, size_t length) {
    Cursor cursor = { body, length, 0, 1 };
    char prefix[TEXT_MAX];
    Builder out;
    Builder message;

    cursor_string(&cursor, prefix, sizeof(prefix));

    if (!cursor.ok || cursor.position != length) {
        stream_refuse(session, index, MOQL_ERROR_CANCELLED);

        return;
    }

    trim_path(prefix);

    const char *suffix = suffix_under(session->source->broadcast, prefix);

    builder_init(&out);
    builder_init(&message);
    put_varint(&message, session->hop);
    put_varint(&message, suffix ? 1 : 0);
    put_message(&out, &message);

    if (suffix) {
        builder_init(&message);
        put_string(&message, suffix);
        put_varint(&message, 0);
        put_varint(&message, 0);
        put_varint(&message, 0);
        put_varint(&out, 0);
        put_message(&out, &message);
    }

    if (stream_queue(session, index, &out) <= 0) {
        stream_refuse(session, index, MOQL_ERROR_CANCELLED);
    }
}

static void handle_track(MoqlSession *session, int index, const unsigned char *body, size_t length) {
    Cursor cursor = { body, length, 0, 1 };
    char broadcast[TEXT_MAX];
    char name[TEXT_MAX];
    Builder out;
    Builder message;

    cursor_string(&cursor, broadcast, sizeof(broadcast));
    cursor_string(&cursor, name, sizeof(name));
    trim_path(broadcast);

    int track = cursor.ok && cursor.position == length ? track_named(session, broadcast, name) : -1;

    if (track < 0) {
        stream_refuse(session, index, MOQL_ERROR_NOT_FOUND);

        return;
    }

    builder_init(&out);
    builder_init(&message);
    put_bytes(&message, (const unsigned char[]){ 0 }, 1);
    put_varint(&message, track == MOQL_TRACK_AUDIO ? MOQL_AUDIO_MAX_AGE_MS : MOQL_TEXT_MAX_AGE_MS);
    put_varint(&message, track == MOQL_TRACK_CATALOG ? 1000 : 48000);
    put_message(&out, &message);

    if (stream_queue(session, index, &out) <= 0) {
        stream_refuse(session, index, MOQL_ERROR_CANCELLED);

        return;
    }

    stream_finish(session, index);
}

static uint64_t first_at_or_after(const ObjectTrack *track, uint64_t group) {
    for (uint64_t sequence = object_track_oldest(track); sequence < track->count; sequence++) {
        const ObjectEntry *entry = object_track_entry(track, sequence);

        if (entry && entry->group >= group) {
            return sequence;
        }
    }

    return track->count;
}

static void handle_subscribe(MoqlSession *session, int index, const unsigned char *body, size_t length) {
    Cursor cursor = { body, length, 0, 1 };
    char broadcast[TEXT_MAX];
    char name[TEXT_MAX];
    int slot = -1;

    uint64_t id = cursor_varint(&cursor);

    cursor_string(&cursor, broadcast, sizeof(broadcast));
    cursor_string(&cursor, name, sizeof(name));
    cursor_u8(&cursor);
    cursor_varint(&cursor);

    uint64_t start_group = cursor_varint(&cursor);
    uint64_t end_group = cursor_varint(&cursor);
    uint64_t start_frame = cursor_varint(&cursor);

    cursor_varint(&cursor);
    trim_path(broadcast);

    int track = cursor.ok && cursor.position == length ? track_named(session, broadcast, name) : -1;

    for (int candidate = 0; candidate < MOQL_SUBSCRIPTIONS; candidate++) {
        if (!session->subscriptions[candidate].used) {
            slot = candidate;

            break;
        }
    }

    if (track < 0 || slot < 0) {
        stream_refuse(session, index, track < 0 ? MOQL_ERROR_NOT_FOUND : MOQL_ERROR_CANCELLED);

        return;
    }

    MoqlSubscription *subscription = &session->subscriptions[slot];
    const ObjectTrack *objects = object_track_of(session, track);

    memset(subscription, 0, sizeof(*subscription));

    subscription->used = 1;
    subscription->id = id;
    subscription->track = track;
    subscription->control = index;
    subscription->group_stream = -1;
    subscription->min_group = start_group;
    subscription->end_group = end_group;

    if (objects && start_group > 0) {
        subscription->next = first_at_or_after(objects, start_group);
        subscription->min_frame = start_frame;
    }
    else if (track == MOQL_TRACK_AUDIO) {
        subscription->next = session->source->live && objects->count > objects->floor ? objects->group_first : objects->count;
    }
    else if (track == MOQL_TRACK_META) {
        subscription->next = objects->count > objects->floor ? objects->count - 1 : objects->count;
    }

    session->streams[index].subscription = slot;
}

static void cancel_subscription(MoqlSession *session, int slot, uint64_t code) {
    MoqlSubscription *subscription = &session->subscriptions[slot];

    if (!subscription->used) {
        return;
    }

    if (subscription->group_stream >= 0) {
        stream_reset(session, subscription->group_stream, code);
    }

    if (subscription->control >= 0) {
        MoqlStream *control = &session->streams[subscription->control];

        control->subscription = -1;

        stream_finish(session, subscription->control);
    }

    subscription->used = 0;
}

static int start_group(MoqlSession *session, MoqlSubscription *subscription, uint64_t group, uint64_t frame_start) {
    Builder out;
    Builder message;
    int index = open_uni(session);

    if (index < 0) {
        return 0;
    }

    session->streams[index].subscription = (int)(subscription - session->subscriptions);
    session->streams[index].group_stream = 1;

    builder_init(&out);
    builder_init(&message);
    put_varint(&out, 0);
    put_varint(&message, subscription->id);
    put_varint(&message, group);
    put_varint(&message, frame_start);
    put_message(&out, &message);
    stream_queue(session, index, &out);

    subscription->group_stream = index;
    subscription->group = group;
    subscription->prev_timestamp = 0;
    session->groups_out++;

    if (!subscription->started && subscription->control >= 0) {
        builder_init(&out);
        builder_init(&message);
        put_varint(&out, 0);
        put_varint(&message, group);
        put_message(&out, &message);

        if (stream_queue(session, subscription->control, &out) > 0) {
            subscription->started = 1;
        }
    }

    return 1;
}

static uint64_t zigzag(uint64_t timestamp, uint64_t previous) {
    return timestamp >= previous ? (timestamp - previous) << 1 : ((previous - timestamp) << 1) - 1;
}

static int queue_frame(MoqlSession *session, int index, uint64_t timestamp, uint64_t previous, const unsigned char *payload, size_t length) {
    Builder out;

    builder_init(&out);
    put_varint(&out, zigzag(timestamp, previous));
    put_varint(&out, length);
    put_bytes(&out, payload, length);

    if (!out.ok) {
        return -1;
    }

    int result = stream_queue(session, index, &out);

    if (result > 0) {
        session->frames_out++;
    }

    return result;
}

static void serve_objects(MoqlSession *session, MoqlSubscription *subscription) {
    const ObjectTrack *track = object_track_of(session, subscription->track);
    int single = subscription->track == MOQL_TRACK_META;

    while (subscription->used && subscription->next < track->count) {
        uint64_t oldest = object_track_oldest(track);
        const ObjectEntry *entry = subscription->next >= oldest ? object_track_entry(track, subscription->next) : NULL;

        if (!entry || (!single && entry->group + 1 < track->group)) {
            uint64_t latest = single ? oldest : (track->group_first > oldest ? track->group_first : oldest);

            if (latest <= subscription->next) {
                return;
            }

            if (subscription->group_stream >= 0) {
                stream_reset(session, subscription->group_stream, MOQL_ERROR_OLD);

                subscription->group_stream = -1;
            }

            subscription->next = latest;
            session->skips++;

            continue;
        }

        if (entry->group < subscription->min_group ||
            (entry->group == subscription->min_group && entry->index < subscription->min_frame)) {
            subscription->next++;

            continue;
        }

        if (subscription->end_group > 0 && entry->group + 1 > subscription->end_group) {
            if (subscription->group_stream >= 0) {
                stream_finish(session, subscription->group_stream);

                subscription->group_stream = -1;
            }

            cancel_subscription(session, (int)(subscription - session->subscriptions), MOQL_ERROR_CANCELLED);

            return;
        }

        if (subscription->group_stream < 0 || subscription->group != entry->group) {
            if (subscription->group_stream >= 0) {
                stream_finish(session, subscription->group_stream);

                subscription->group_stream = -1;
            }

            if (!start_group(session, subscription, entry->group, entry->index)) {
                return;
            }
        }

        size_t length = object_track_copy(track, entry, scratch, sizeof(scratch));
        const unsigned char *payload = scratch;
        uint64_t timestamp = entry->timestamp;

        if (subscription->track == MOQL_TRACK_AUDIO) {
            long used = length > 1 && scratch[0] == 0 ? varint_decode(scratch + 1, length - 1, &timestamp) : 0;

            if (used <= 0) {
                subscription->next++;

                continue;
            }

            payload = scratch + 1 + used;
            length -= 1 + (size_t)used;
        }

        int result = queue_frame(session, subscription->group_stream, timestamp, subscription->prev_timestamp, payload, length);

        if (result == 0) {
            return;
        }

        subscription->prev_timestamp = timestamp;
        subscription->next++;

        if (single && subscription->group_stream >= 0) {
            stream_finish(session, subscription->group_stream);

            subscription->group_stream = -1;
        }
    }
}

static void serve_catalog(MoqlSession *session, MoqlSubscription *subscription) {
    if (subscription->catalog_sent == session->source->catalog_version || session->source->catalog_length == 0) {
        return;
    }

    if (subscription->group_stream >= 0) {
        stream_finish(session, subscription->group_stream);

        subscription->group_stream = -1;
    }

    if (!start_group(session, subscription, session->source->catalog_version, 0)) {
        return;
    }

    if (queue_frame(session, subscription->group_stream, 0, 0, session->source->catalog, session->source->catalog_length) > 0) {
        subscription->catalog_sent = session->source->catalog_version;
    }

    stream_finish(session, subscription->group_stream);

    subscription->group_stream = -1;
}

static void process_input(MoqlSession *session, int index) {
    MoqlStream *stream = &session->streams[index];

    while (stream->used && stream->in_length > 0) {
        size_t consumed = 0;

        if (stream->kind < 0) {
            uint64_t kind = 0;
            long used = varint_decode(stream->in, stream->in_length, &kind);

            if (used <= 0) {
                break;
            }

            consumed = (size_t)used;
            stream->kind = kind > 0xFF ? 0xFF : (int)kind;

            int bidi = bidi_stream(stream->id);
            int known = bidi ? (stream->kind == 1 || stream->kind == 2 || stream->kind == 6) : stream->kind == 1;

            if (!known) {
                stream_refuse(session, index, MOQL_ERROR_CANCELLED);

                return;
            }
        }
        else {
            const unsigned char *body = NULL;
            size_t length = 0;
            long used = message_body(stream->in, stream->in_length, &body, &length);

            if (used < 0) {
                stream_refuse(session, index, MOQL_ERROR_CANCELLED);

                return;
            }

            if (used == 0) {
                break;
            }

            consumed = (size_t)used;

            if (bidi_stream(stream->id) && stream->kind == 1 && stream->send_offset == 0 && stream->pending_length == 0) {
                handle_announce(session, index, body, length);
            }
            else if (bidi_stream(stream->id) && stream->kind == 6 && !stream->fin_queued && !stream->send_done) {
                handle_track(session, index, body, length);
            }
            else if (bidi_stream(stream->id) && stream->kind == 2 && stream->subscription < 0 && stream->send_offset == 0) {
                handle_subscribe(session, index, body, length);
            }
        }

        if (!stream->used || consumed > stream->in_length) {
            return;
        }

        memmove(stream->in, stream->in + consumed, stream->in_length - consumed);

        stream->in_length -= consumed;
    }
}

static void stream_ended(MoqlSession *session, int index) {
    MoqlStream *stream = &session->streams[index];

    if (stream->subscription >= 0 && !stream->group_stream) {
        cancel_subscription(session, stream->subscription, MOQL_ERROR_CANCELLED);
    }
    else if (bidi_stream(stream->id) && stream->kind == 1) {
        stream_finish(session, index);
    }
    else if (bidi_stream(stream->id) && stream->kind != 6) {
        stream_finish(session, index);
    }
}

static int fail(MoqlSession *session, const char *why) {
    session->error = why;

    return -1;
}

static void peer_reset(MoqlSession *session, int index) {
    if (index < 0 || !client_stream(session->streams[index].id) || session->streams[index].recv_done) {
        return;
    }

    session->streams[index].recv_done = 1;
    session->streams[index].in_length = 0;

    stream_ended(session, index);
    maybe_release(session, index);
}

static void peer_stop(MoqlSession *session, int index, uint64_t code) {
    if (index < 0) {
        return;
    }

    MoqlStream *stream = &session->streams[index];

    if (stream->subscription >= 0 && !stream->group_stream) {
        cancel_subscription(session, stream->subscription, code);
    }
    else if (stream->subscription >= 0 && session->subscriptions[stream->subscription].used &&
             session->subscriptions[stream->subscription].group_stream == index) {
        MoqlSubscription *subscription = &session->subscriptions[stream->subscription];

        subscription->min_group = subscription->group + 1;
        subscription->min_frame = 0;
        subscription->group_stream = -1;
    }

    stream_reset(session, index, code);
}

static int receive_stream(MoqlSession *session, const QmuxFrame *frame) {
    unsigned char control[32];

    if (!client_stream(frame->stream)) {
        return fail(session, "data on a stream the relay opened");
    }

    uint64_t ordinal = frame->stream / 4;
    uint64_t granted = bidi_stream(frame->stream) ? session->max_bidi_granted : session->max_uni_granted;

    if (ordinal >= granted) {
        return fail(session, "stream limit exceeded");
    }

    int index = stream_find(session, frame->stream);

    if (index < 0 && recently_closed(session, frame->stream)) {
        session->recv_total += frame->length;

        return 0;
    }

    if (index < 0) {
        index = stream_add(session, frame->stream);

        if (index < 0) {
            return fail(session, "too many open streams");
        }
    }

    MoqlStream *stream = &session->streams[index];

    if (stream->recv_done) {
        return 0;
    }

    if (frame->offset != stream->recv_offset) {
        return fail(session, "stream data out of order");
    }

    if (frame->offset + frame->length > stream->recv_window || session->recv_total + frame->length > session->recv_max) {
        return fail(session, "flow control exceeded");
    }

    if (frame->length > 0) {
        if (!stream->in) {
            stream->in = malloc(MOQL_IN_MAX);

            if (!stream->in) {
                return fail(session, "out of memory");
            }
        }

        if (frame->length > MOQL_IN_MAX - stream->in_length) {
            return fail(session, "control message too large");
        }

        memcpy(stream->in + stream->in_length, frame->data, frame->length);

        stream->in_length += frame->length;
    }

    stream->recv_offset += frame->length;
    session->recv_total += frame->length;

    if (frame->fin) {
        stream->recv_done = 1;
    }

    process_input(session, index);

    if (stream->used && stream->recv_done && frame->fin) {
        stream_ended(session, index);
    }

    if (stream->used && !stream->recv_done && stream->recv_window - stream->recv_offset < RECV_STREAM_WINDOW / 2) {
        stream->recv_window = stream->recv_offset + RECV_STREAM_WINDOW;

        queue_control(session, control, qmux_max_stream_data(control, stream->id, stream->recv_window));
    }

    if (session->recv_max - session->recv_total < RECV_DATA_WINDOW / 2) {
        session->recv_max = session->recv_total + RECV_DATA_WINDOW;

        queue_control(session, control, qmux_max_data(control, session->recv_max));
    }

    if (stream->used) {
        maybe_release(session, index);
    }

    return 0;
}

int moql_receive(MoqlSession *session, const unsigned char *record, size_t length, int64_t now) {
    size_t position = 0;
    unsigned char control[32];

    (void)now;

    session->records_in++;

    while (position < length) {
        QmuxFrame frame;
        long used = qmux_decode(record + position, length - position, &frame);

        if (used <= 0) {
            return fail(session, "malformed frame");
        }

        position += (size_t)used;

        if (frame.type == QMUX_FRAME_PADDING) {
            continue;
        }

        if (!session->peer_known && frame.type != QMUX_FRAME_PARAMETERS) {
            return fail(session, "transport parameters must come first");
        }

        int index = frame.type == QMUX_FRAME_STREAM ? -1 : stream_find(session, frame.stream);

        switch (frame.type) {
            case QMUX_FRAME_PARAMETERS:
                if (session->peer_known || qmux_params_parse(frame.data, frame.length, &session->peer) < 0) {
                    return fail(session, "bad transport parameters");
                }

                session->peer_known = 1;

                for (int stream = 0; stream < MOQL_STREAMS; stream++) {
                    MoqlStream *open = &session->streams[stream];
                    uint64_t limit = !client_stream(open->id) ? session->peer.initial_max_stream_data_uni
                        : session->peer.initial_max_stream_data_bidi_local;

                    if (open->used && limit > open->send_limit) {
                        open->send_limit = limit;
                    }
                }

                break;

            case QMUX_FRAME_STREAM:
                if (receive_stream(session, &frame) < 0) {
                    return -1;
                }

                break;

            case QMUX_FRAME_RESET_STREAM:
            case QMUX_FRAME_RESET_STREAM_AT:
                peer_reset(session, index);

                break;

            case QMUX_FRAME_STOP_SENDING:
                peer_stop(session, index, frame.value);

                break;

            case QMUX_FRAME_MAX_DATA:
                if (frame.value > session->peer.initial_max_data) {
                    session->peer.initial_max_data = frame.value;
                }

                break;

            case QMUX_FRAME_MAX_STREAM_DATA:
                if (index >= 0 && frame.value > session->streams[index].send_limit) {
                    session->streams[index].send_limit = frame.value;
                }

                break;

            case QMUX_FRAME_MAX_STREAMS_UNI:
                if (frame.value > session->peer.initial_max_streams_uni) {
                    session->peer.initial_max_streams_uni = frame.value;
                }

                break;

            case QMUX_FRAME_MAX_STREAMS_BIDI:
            case QMUX_FRAME_DATA_BLOCKED:
            case QMUX_FRAME_STREAM_DATA_BLOCKED:
            case QMUX_FRAME_STREAMS_BLOCKED:
            case QMUX_FRAME_DATAGRAM:
                break;

            case QMUX_FRAME_PING:
                queue_control(session, control, qmux_ping(control, 1, frame.value));

                break;

            case QMUX_FRAME_PONG:
                if (frame.value > session->ping_sent) {
                    return fail(session, "a pong for a ping never sent");
                }

                break;

            case QMUX_FRAME_CONNECTION_CLOSE:
                session->closing = 1;

                return 1;

            default:
                break;
        }
    }

    return 0;
}

static void emit_records(MoqlSession *session, int64_t now) {
    size_t limit = MOQL_RECORD;

    while (session->out_length + limit + 16 <= MOQL_OUT_MAX) {
        unsigned char record[MOQL_RECORD];
        size_t length = 0;
        int full = 0;

        if (session->control_length > 0) {
            memcpy(record, session->control, session->control_length);

            length = session->control_length;
            session->control_length = 0;
        }

        for (int pass = 0; pass < 2 && session->peer_known && !full; pass++) {
            for (int index = 0; index < MOQL_STREAMS && !full; index++) {
                MoqlStream *stream = &session->streams[index];

                if (!stream->used || (pass == 0) != (stream->group_stream == 0)) {
                    continue;
                }

                if (stream->stop_pending) {
                    if (length + 24 > limit) {
                        full = 1;

                        break;
                    }

                    length += qmux_stop_sending(record + length, stream->id, stream->stop_code);
                    stream->stop_pending = 0;
                }

                if (stream->reset_pending) {
                    if (length + 32 > limit) {
                        full = 1;

                        break;
                    }

                    length += qmux_reset_stream(record + length, stream->id, stream->reset_code, stream->send_offset);
                    stream->reset_pending = 0;
                    stream->send_done = 1;

                    maybe_release(session, index);

                    continue;
                }

                if (stream->send_done || (stream->pending_length == 0 && !stream->fin_queued)) {
                    maybe_release(session, index);

                    continue;
                }

                uint64_t stream_credit = stream->send_limit > stream->send_offset ? stream->send_limit - stream->send_offset : 0;
                uint64_t connection_credit = session->peer.initial_max_data > session->send_total
                    ? session->peer.initial_max_data - session->send_total : 0;
                size_t chunk = stream->pending_length;

                if (chunk > stream_credit) {
                    chunk = (size_t)stream_credit;
                }

                if (chunk > connection_credit) {
                    chunk = (size_t)connection_credit;
                }

                if (length + 25 + 1 > limit) {
                    full = 1;

                    break;
                }

                if (chunk > limit - length - 25) {
                    chunk = limit - length - 25;
                }

                int fin = stream->fin_queued && chunk == stream->pending_length;

                if (chunk == 0 && !fin) {
                    continue;
                }

                length += qmux_stream_header(record + length, stream->id, stream->send_offset, chunk, fin);

                memcpy(record + length, stream->pending, chunk);
                memmove(stream->pending, stream->pending + chunk, stream->pending_length - chunk);

                length += chunk;
                stream->pending_length -= chunk;
                stream->send_offset += chunk;
                session->send_total += chunk;

                if (fin) {
                    stream->send_done = 1;
                    stream->fin_queued = 0;

                    maybe_release(session, index);
                }
                else if (stream->pending_length > 0) {
                    full = chunk > 0 && length + 26 > limit;
                }
            }
        }

        if (length == 0) {
            break;
        }

        unsigned char header[10];
        size_t header_length = ws_frame_header(header, WS_BINARY, length);

        memcpy(session->out + session->out_length, header, header_length);
        memcpy(session->out + session->out_length + header_length, record, length);

        session->out_length += header_length + length;
        session->records_out++;
        session->last_record_ms = now;
    }
}

static void emit_native(MoqlSession *session) {
    const MoqlTransport *transport = session->transport;

    for (int index = 0; index < MOQL_STREAMS; index++) {
        MoqlStream *stream = &session->streams[index];

        if (!stream->used) {
            continue;
        }

        if (stream->stop_pending) {
            stream->stop_pending = 0;

            transport->stop(transport->context, stream->id, stream->stop_code);
        }

        if (stream->reset_pending) {
            stream->reset_pending = 0;
            stream->send_done = 1;
            stream->woken = 0;

            transport->reset(transport->context, stream->id, stream->reset_code);
        }
        else if (!stream->send_done && !stream->woken && (stream->pending_length > 0 || stream->fin_queued)) {
            stream->woken = 1;

            transport->wake(transport->context, stream->id);
        }

        maybe_release(session, index);
    }
}

static void open_setup(MoqlSession *session) {
    int setup = open_uni(session);

    if (setup < 0) {
        return;
    }

    Builder out;
    Builder message;

    builder_init(&out);
    builder_init(&message);
    put_varint(&out, 1);
    put_varint(&message, 0);
    put_message(&out, &message);
    stream_queue(session, setup, &out);
    stream_finish(session, setup);

    session->setup_opened = 1;
}

static MoqlSession *session_new(MoqlSource *source, int version, uint64_t hop, int64_t now) {
    MoqlSession *session = calloc(1, sizeof(MoqlSession));

    if (!session) {
        return NULL;
    }

    session->source = source;
    session->version = version;
    session->hop = hop;
    session->recv_max = RECV_DATA_WINDOW;
    session->max_bidi_granted = BIDI_GRANT;
    session->max_uni_granted = UNI_GRANT;
    session->last_record_ms = now;

    return session;
}

MoqlSession *moql_create_native(MoqlSource *source, int version, uint64_t hop, const MoqlTransport *transport, int64_t now) {
    MoqlSession *session = session_new(source, version, hop, now);

    if (!session) {
        return NULL;
    }

    session->transport = transport;
    session->peer_known = 1;
    session->peer.initial_max_data = UNLIMITED;
    session->peer.initial_max_stream_data_bidi_local = UNLIMITED;
    session->peer.initial_max_stream_data_bidi_remote = UNLIMITED;
    session->peer.initial_max_stream_data_uni = UNLIMITED;
    session->peer.initial_max_streams_bidi = UNLIMITED;
    session->peer.initial_max_streams_uni = UNLIMITED;
    session->recv_max = UNLIMITED;
    session->max_bidi_granted = UNLIMITED;
    session->max_uni_granted = UNLIMITED;

    return session;
}

MoqlSession *moql_create(MoqlSource *source, int version, uint64_t hop, int64_t now) {
    MoqlSession *session = session_new(source, version, hop, now);
    QmuxParams ours;
    unsigned char frame[160];

    if (!session) {
        return NULL;
    }

    session->out = malloc(MOQL_OUT_MAX);

    if (!session->out) {
        free(session);

        return NULL;
    }

    memset(&ours, 0, sizeof(ours));

    ours.max_idle_timeout = 60000;
    ours.initial_max_data = RECV_DATA_WINDOW;
    ours.initial_max_stream_data_bidi_local = RECV_STREAM_WINDOW;
    ours.initial_max_stream_data_bidi_remote = RECV_STREAM_WINDOW;
    ours.initial_max_stream_data_uni = RECV_STREAM_WINDOW;
    ours.initial_max_streams_bidi = BIDI_GRANT;
    ours.initial_max_streams_uni = UNI_GRANT;
    ours.max_record_size = MOQL_RECORD;

    queue_control(session, frame, qmux_params_frame(frame, sizeof(frame), &ours));

    int setup = stream_add(session, 3);

    if (setup >= 0) {
        Builder out;
        Builder message;

        session->opened_uni = 1;

        builder_init(&out);
        builder_init(&message);
        put_varint(&out, 1);
        put_varint(&message, 0);
        put_message(&out, &message);
        stream_queue(session, setup, &out);
        stream_finish(session, setup);
    }

    return session;
}

void moql_free(MoqlSession *session) {
    if (!session) {
        return;
    }

    for (int index = 0; index < MOQL_STREAMS; index++) {
        free(session->streams[index].in);
        free(session->streams[index].pending);
    }

    free(session->out);
    free(session);
}

void moql_pump(MoqlSession *session, int64_t now) {
    unsigned char frame[16];

    if (session->transport && !session->setup_opened && !session->closing) {
        open_setup(session);
    }

    if (session->peer_known && !session->closing) {
        for (int index = 0; index < MOQL_SUBSCRIPTIONS; index++) {
            MoqlSubscription *subscription = &session->subscriptions[index];

            if (!subscription->used) {
                continue;
            }

            if (subscription->track == MOQL_TRACK_CATALOG) {
                serve_catalog(session, subscription);
            }
            else {
                serve_objects(session, subscription);
            }
        }

        if (!session->transport && now - session->last_record_ms > MOQL_PING_MS && session->control_length == 0) {
            session->ping_sent++;

            queue_control(session, frame, qmux_ping(frame, 0, session->ping_sent));
        }
    }

    if (session->transport) {
        emit_native(session);
    }
    else {
        emit_records(session, now);
    }
}

void moql_raw(MoqlSession *session, const unsigned char *bytes, size_t length) {
    if (session->out && length <= MOQL_OUT_MAX - session->out_length) {
        memcpy(session->out + session->out_length, bytes, length);

        session->out_length += length;
    }
}

void moql_close(MoqlSession *session, uint64_t code, const char *reason) {
    unsigned char frame[128];

    session->closing = 1;

    if (!session->transport) {
        queue_control(session, frame, qmux_close(frame, sizeof(frame), code, reason));
        emit_records(session, session->last_record_ms);
    }
}

int moql_stream_data(MoqlSession *session, uint64_t stream, const unsigned char *data, size_t length, int fin) {
    int index = stream_find(session, stream);
    QmuxFrame frame;

    memset(&frame, 0, sizeof(frame));

    frame.type = QMUX_FRAME_STREAM;
    frame.stream = stream;
    frame.offset = index >= 0 ? session->streams[index].recv_offset : 0;
    frame.data = data;
    frame.length = length;
    frame.fin = fin;

    if (index < 0 && length == 0 && !fin) {
        return 0;
    }

    session->records_in++;

    return receive_stream(session, &frame);
}

void moql_stream_reset(MoqlSession *session, uint64_t stream) {
    peer_reset(session, stream_find(session, stream));
}

void moql_stream_stop(MoqlSession *session, uint64_t stream, uint64_t code) {
    peer_stop(session, stream_find(session, stream), code);
}

int moql_stream_send(MoqlSession *session, uint64_t stream, size_t space, MoqlBufferFn buffer, void *context) {
    int index = stream_find(session, stream);
    MoqlStream *open = index >= 0 ? &session->streams[index] : NULL;

    if (!open || open->send_done || open->reset_pending || (open->pending_length == 0 && !open->fin_queued)) {
        if (open) {
            open->woken = 0;
        }

        buffer(context, 0, 0, 0);

        return 0;
    }

    size_t chunk = open->pending_length < space ? open->pending_length : space;
    int fin = open->fin_queued && chunk == open->pending_length;
    int more = chunk < open->pending_length;

    if (chunk == 0 && !fin) {
        buffer(context, 0, 0, 1);

        return 0;
    }

    unsigned char *target = buffer(context, chunk, fin, more);

    if (!target) {
        return -1;
    }

    memcpy(target, open->pending, chunk);
    memmove(open->pending, open->pending + chunk, open->pending_length - chunk);

    open->pending_length -= chunk;
    open->send_offset += chunk;
    session->send_total += chunk;

    if (fin) {
        open->send_done = 1;
        open->fin_queued = 0;
        open->woken = 0;

        maybe_release(session, index);
    }
    else if (!more) {
        open->woken = 0;
    }

    return 0;
}

size_t moql_pending(const MoqlSession *session) {
    return session->out_length - session->out_offset;
}

void moql_consumed(MoqlSession *session, size_t length) {
    session->out_offset += length;

    if (session->out_offset >= session->out_length) {
        session->out_offset = 0;
        session->out_length = 0;
    }
    else if (session->out_offset > MOQL_OUT_MAX / 2) {
        memmove(session->out, session->out + session->out_offset, session->out_length - session->out_offset);

        session->out_length -= session->out_offset;
        session->out_offset = 0;
    }
}
