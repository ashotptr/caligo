#define _GNU_SOURCE

#include "relay.h"

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/random.h>
#include <time.h>
#include <unistd.h>

typedef struct {
    char data[3840];
    size_t length;
} Text;

#define MOQ_PROTOCOL "caligo-objects-0"
#define LITE_PROTOCOL_02 "qmux-02.moq-lite-06"
#define LITE_PROTOCOL_01 "qmux-01.moq-lite-06"
#define LITE_INPUT (MOQL_RECORD * 2 + 64)
#define MOQ_OBJECT 0x01
#define MOQ_PROBE 0x02
#define TRACK_CATALOG 0
#define TRACK_AUDIO 1
#define TRACK_META 2

static Config config;
static Mount mp3 = { .path = "/live/", .type = "audio/mpeg" };
static Mount opus = { .path = "/live/opus", .type = "audio/ogg" };
static Mp3Framer framer;
static Mp3Framer live_framer;
static OggReader ogg_reader;
static OpusSplicer splicer;
static ObjectTrack audio_track;
static ObjectTrack meta_track;
static char catalog_json[640];
static size_t catalog_length;
static uint64_t catalog_version;
static uint64_t moq_title_id;
static uint64_t audio_clock_end;
static MoqlSource lite_source = { .broadcast = "caligo/radio/main" };
static uint64_t lite_hop;
static unsigned char object_buffer[OPUS_PACKET_MAX + 32];
static Upstream audio;
static Upstream events;
static Resolver resolver;
static Stats stats;
static Session sessions[SESSION_SLOTS];
static Listener **listeners;
static RadioEvent radio_title;
static int have_radio_title;
static int epoll_fd = -1;
static int listen_fd = -1;
static int listen_kind = KIND_LISTEN;
static int resolver_kind = KIND_RESOLVER;
static int quic_kind = KIND_QUIC;
static volatile sig_atomic_t running = 1;
static struct sockaddr_storage radio_address;
static socklen_t radio_address_length;
static int64_t radio_address_ms;

static const char METADATA_DONE[] =
    "<?xml version=\"1.0\"?>\n<iceresponse><message>Metadata update successful</message><return>1</return></iceresponse>\n";

static int64_t now_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static void say(const char *format, ...) {
    char text[512];
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);

    fprintf(stderr, "[relay] %s\n", text);
}

static const char *env_text(const char *name, const char *fallback) {
    const char *value = getenv(name);

    return value && *value ? value : fallback;
}

static long env_number(const char *name, long fallback, long minimum, long maximum) {
    const char *value = getenv(name);

    if (!value || !*value) {
        return fallback;
    }

    char *end = NULL;
    long number = strtol(value, &end, 10);

    if (end == value || *end != '\0' || number < minimum || number > maximum) {
        say("%s=%s is not a number between %ld and %ld, using %ld", name, value, minimum, maximum, fallback);

        return fallback;
    }

    return number;
}

static size_t power_of_two_at_least(size_t value) {
    size_t power = 65536;

    while (power < value) {
        power <<= 1;
    }

    return power;
}

static void load_config(void) {
    snprintf(config.listen_host, sizeof(config.listen_host), "%s", env_text("RELAY_LISTEN_HOST", "0.0.0.0"));
    snprintf(config.source_host, sizeof(config.source_host), "%s", env_text("RELAY_SOURCE_HOST", "caligo"));
    snprintf(config.source_path, sizeof(config.source_path), "%s", env_text("RELAY_SOURCE_PATH", "/radio/"));
    snprintf(config.events_path, sizeof(config.events_path), "%s", env_text("RELAY_EVENTS_PATH", "/radio/events"));
    snprintf(config.station, sizeof(config.station), "%s", env_text("RELAY_STATION", "Caligo Radio"));
    snprintf(config.source_user, sizeof(config.source_user), "%s", env_text("RELAY_SOURCE_USER", "source"));
    snprintf(config.source_password, sizeof(config.source_password), "%s", env_text("RELAY_SOURCE_PASSWORD", ""));

    config.listen_port = (int)env_number("RELAY_LISTEN_PORT", 8000, 1, 65535);
    config.source_port = (int)env_number("RELAY_SOURCE_PORT", 9001, 1, 65535);
    config.burst_bytes = (size_t)env_number("RELAY_BURST_BYTES", 65536, 0, 4 * 1024 * 1024);
    config.ring_bytes = power_of_two_at_least((size_t)env_number("RELAY_RING_BYTES", 4 * 1024 * 1024, 65536, 256 * 1024 * 1024));
    config.max_lag_bytes = (size_t)env_number("RELAY_MAX_LAG_BYTES", 1024 * 1024, 16384, 256 * 1024 * 1024);
    config.max_listeners = (int)env_number("RELAY_MAX_LISTENERS", 2000, 1, 100000);
    config.send_buffer = (int)env_number("RELAY_SEND_BUFFER", 262144, 0, 16 * 1024 * 1024);
    config.icy_metaint = (int)env_number("RELAY_ICY_METAINT", 16000, 256, 1024 * 1024);
    config.source_timeout = (int)env_number("RELAY_SOURCE_TIMEOUT", 10, 2, 3600);
    config.source_grace = (int)env_number("RELAY_SOURCE_GRACE", 10, 0, 3600);
    config.source_max_ahead = (int)env_number("RELAY_SOURCE_MAX_AHEAD", 15, 1, 3600);
    config.request_timeout = (int)env_number("RELAY_REQUEST_TIMEOUT", 10, 1, 3600);
    config.session_ttl = (int)env_number("RELAY_SESSION_TTL", 600, 1, 86400);
    config.moq_group_ms = (int)env_number("RELAY_MOQ_GROUP_MS", 1000, 100, 10000);
    config.moq_ping = (int)env_number("RELAY_MOQ_PING", 15, 1, 3600);
    config.moq_idle = (int)env_number("RELAY_MOQ_IDLE", 60, 2, 86400);
    config.moq_send_buffer = (int)env_number("RELAY_MOQ_SEND_BUFFER", 16384, 0, 16 * 1024 * 1024);
    config.quic_port = (int)env_number("RELAY_QUIC_PORT", config.listen_port, 0, 65535);
    config.quic_max_sessions = (int)env_number("RELAY_QUIC_MAX_SESSIONS", 500, 1, 100000);
    config.quic_idle = (int)env_number("RELAY_QUIC_IDLE", 30, 2, 3600);

    snprintf(config.quic_cert, sizeof(config.quic_cert), "%s", env_text("RELAY_QUIC_CERT", ""));
    snprintf(config.quic_key, sizeof(config.quic_key), "%s", env_text("RELAY_QUIC_KEY", ""));

    if (config.max_lag_bytes > config.ring_bytes - MAX_CHUNK) {
        config.max_lag_bytes = config.ring_bytes - MAX_CHUNK;
    }

    if (config.burst_bytes > config.max_lag_bytes / 2) {
        config.burst_bytes = config.max_lag_bytes / 2;
    }

    if (config.source_password[0] != '\0' && strlen(config.source_password) < 12) {
        say("RELAY_SOURCE_PASSWORD is shorter than 12 characters, sources stay switched off");

        config.source_password[0] = '\0';
    }
}

static void watch(int fd, uint32_t interest, void *tag, int modify) {
    struct epoll_event event;

    memset(&event, 0, sizeof(event));

    event.events = interest;
    event.data.ptr = tag;

    if (epoll_ctl(epoll_fd, modify ? EPOLL_CTL_MOD : EPOLL_CTL_ADD, fd, &event) < 0) {
        say("epoll_ctl failed: %s", strerror(errno));
    }
}

static Session *session_find(uint64_t key, int64_t now) {
    for (int probe = 0; probe < SESSION_PROBES; probe++) {
        Session *session = &sessions[(key + (uint64_t)probe) % SESSION_SLOTS];

        if (session->key == key && now - session->used_ms <= (int64_t)config.session_ttl * 1000) {
            return session;
        }
    }

    return NULL;
}

static void session_store(uint64_t key, uint64_t base, int64_t now) {
    Session *target = NULL;

    for (int probe = 0; probe < SESSION_PROBES; probe++) {
        Session *session = &sessions[(key + (uint64_t)probe) % SESSION_SLOTS];

        if (session->key == key) {
            target = session;

            break;
        }

        if (!target || session->used_ms < target->used_ms) {
            target = session;
        }
    }

    target->key = key;
    target->base = base;
    target->used_ms = now;
}

static const char *relay_path(const char *path) {
    if (strncmp(path, "/live", 5) == 0 && (path[5] == '/' || path[5] == '\0')) {
        path += 5;
    }

    return *path == '\0' ? "/" : path;
}

static Mount *mount_named(const char *path) {
    if (strcmp(path, "/") == 0 || strcmp(path, "/stream") == 0 || strcmp(path, "/mp3") == 0) {
        return &mp3;
    }

    return strcmp(path, "/opus") == 0 ? &opus : NULL;
}

static int opus_available(void) {
    return opus.source.connection != NULL && opus.on_air && splicer.have_head;
}

static const StreamTitle *title_at_head(const Mount *mount) {
    if (mount == &opus && opus.follows) {
        mount = &mp3;
    }

    return stream_title_at(&mount->ring, mount->ring.head);
}

static const StreamTitle *audible_title_of(const Mount *mount) {
    if (mount == &opus && opus.follows) {
        mount = &mp3;
    }

    uint64_t position = mount->ring.head > config.burst_bytes ? mount->ring.head - config.burst_bytes : 0;

    return stream_title_at(&mount->ring, position);
}

static void copy_utf8(char *target, size_t capacity, const char *source, size_t length) {
    if (length >= capacity) {
        length = capacity - 1;

        while (length > 0 && ((unsigned char)source[length] & 0xC0) == 0x80) {
            length--;
        }
    }

    memcpy(target, source, length);

    target[length] = '\0';
}

static void source_lost(Mount *mount, const char *why);

static void listener_close(Listener *listener) {
    if (listener->mode == LISTENER_STREAM) {
        listener->mount->listeners--;
    }
    else if (listener->mode == LISTENER_EVENTS) {
        stats.event_listeners--;
    }
    else if (listener->mode == LISTENER_SOURCE && listener->mount->source.connection == listener) {
        source_lost(listener->mount, "closed the connection");
    }
    else if (listener->mode == LISTENER_MOQ) {
        stats.moq_connections--;
    }
    else if (listener->mode == LISTENER_MOQL) {
        stats.lite_connections--;
    }

    moql_free(listener->moql);
    free(listener->ws_in);

    epoll_ctl(epoll_fd, EPOLL_CTL_DEL, listener->fd, NULL);
    close(listener->fd);

    listeners[listener->slot] = NULL;

    free(listener);
}

static void listener_arm(Listener *listener, int writable) {
    if (listener->writable_armed == writable) {
        return;
    }

    listener->writable_armed = writable;

    watch(listener->fd, EPOLLIN | EPOLLRDHUP | (writable ? EPOLLOUT : 0), listener, 1);
}

static size_t listener_room(const Listener *listener) {
    return OUT_MAX - listener->out_length;
}

static void listener_add(Listener *listener, const void *bytes, size_t length) {
    if (length > listener_room(listener)) {
        length = listener_room(listener);
    }

    memcpy(listener->out + listener->out_length, bytes, length);

    listener->out_length += length;
}

static void listener_print(Listener *listener, const char *format, ...) {
    va_list arguments;
    size_t room = listener_room(listener);

    va_start(arguments, format);

    int written = vsnprintf((char *)listener->out + listener->out_length, room, format, arguments);

    va_end(arguments);

    if (written > 0) {
        listener->out_length += (size_t)written < room ? (size_t)written : room - 1;
    }
}

static void text_add(Text *text, const char *format, ...) {
    va_list arguments;
    size_t room = sizeof(text->data) - text->length;

    if (room <= 1) {
        return;
    }

    va_start(arguments, format);

    int written = vsnprintf(text->data + text->length, room, format, arguments);

    va_end(arguments);

    if (written > 0) {
        text->length += (size_t)written < room ? (size_t)written : room - 1;
    }
}

static void text_json(Text *text, const char *value, size_t limit) {
    char cut[STREAM_TEXT];
    char escaped[STREAM_TEXT * 6 + 1];

    copy_utf8(cut, limit + 1 < sizeof(cut) ? limit + 1 : sizeof(cut), value, strlen(value));
    json_escape(escaped, sizeof(escaped), cut);
    text_add(text, "\"%s\"", escaped);
}

static void add_title_json(Listener *listener, const StreamTitle *title) {
    char artist[STREAM_TEXT * 2];
    char album[STREAM_TEXT * 2];
    char name[STREAM_TEXT * 2];

    if (!title) {
        listener_print(listener, "null");

        return;
    }

    json_escape(artist, sizeof(artist), title->artist);
    json_escape(album, sizeof(album), title->album);
    json_escape(name, sizeof(name), title->title);

    listener_print(listener,
        "{\"artist\":\"%s\",\"album\":\"%s\",\"title\":\"%s\",\"is_request\":%s}",
        artist, album, name, title->is_request ? "true" : "false");
}

static void add_icy_block(Listener *listener) {
    const StreamTitle *title = stream_title_at(&listener->mount->ring, listener->position);
    unsigned char block[1 + 16 * 64];
    size_t length = 0;

    memset(block, 0, sizeof(block));

    if (title && title->id != listener->icy_title_id) {
        char text[STREAM_TEXT * 2 + 4];

        if (title->artist[0] && strcmp(title->artist, "Unknown") != 0) {
            snprintf(text, sizeof(text), "%s - %s", title->artist, title->title);
        }
        else {
            snprintf(text, sizeof(text), "%s", title->title);
        }

        for (char *cursor = text; *cursor; cursor++) {
            if (cursor[0] == '\'' && cursor[1] == ';') {
                cursor[1] = ',';
            }
        }

        int written = snprintf((char *)block + 1, sizeof(block) - 1, "StreamTitle='%s';", text);

        length = written > 0 ? ((size_t)written < sizeof(block) - 1 ? (size_t)written : sizeof(block) - 2) : 0;
        listener->icy_title_id = title->id;
    }

    size_t units = (length + 15) / 16;

    block[0] = (unsigned char)units;

    if (listener->chunked) {
        listener_print(listener, "%zx\r\n", 1 + units * 16);
    }

    listener_add(listener, block, 1 + units * 16);

    if (listener->chunked) {
        listener_add(listener, "\r\n", 2);
    }

    listener->icy_left = (size_t)config.icy_metaint;
}

static int moq_next(Listener *listener);

static void listener_flush(Listener *listener) {
    for (;;) {
        if (listener->out_offset < listener->out_length) {
            ssize_t sent = send(
                listener->fd, listener->out + listener->out_offset, listener->out_length - listener->out_offset,
                MSG_NOSIGNAL);

            if (sent < 0) {
                if (errno == EAGAIN || errno == EWOULDBLOCK) {
                    listener_arm(listener, 1);
                }
                else if (errno != EINTR) {
                    listener_close(listener);
                }

                return;
            }

            listener->out_offset += (size_t)sent;

            continue;
        }

        listener->out_length = 0;
        listener->out_offset = 0;

        if (listener->mode == LISTENER_REPLY) {
            shutdown(listener->fd, SHUT_WR);

            listener->mode = LISTENER_DRAIN;
            listener->accepted_ms = now_ms();
        }

        if (listener->mode == LISTENER_MOQ) {
            if (moq_next(listener)) {
                continue;
            }

            listener_arm(listener, 0);

            return;
        }

        if (listener->mode == LISTENER_MOQL) {
            MoqlSession *session = listener->moql;

            if (moql_pending(session) == 0) {
                moql_pump(session, now_ms());
            }

            size_t pending = moql_pending(session);

            if (pending == 0) {
                if (session->closing) {
                    unsigned char frame[4];
                    unsigned char code[2] = { 0x03, 0xE8 };

                    listener_add(listener, frame, ws_frame_header(frame, WS_CLOSE, sizeof(code)));
                    listener_add(listener, code, sizeof(code));

                    stats.lite_connections--;
                    listener->mode = LISTENER_REPLY;

                    continue;
                }

                listener_arm(listener, 0);

                return;
            }

            ssize_t sent = send(listener->fd, session->out + session->out_offset, pending, MSG_NOSIGNAL);

            if (sent < 0) {
                if (errno == EAGAIN || errno == EWOULDBLOCK) {
                    listener_arm(listener, 1);
                }
                else if (errno != EINTR) {
                    listener_close(listener);
                }

                return;
            }

            moql_consumed(session, (size_t)sent);

            continue;
        }

        if (listener->mode != LISTENER_STREAM) {
            listener_arm(listener, 0);

            return;
        }

        Stream *ring = &listener->mount->ring;

        if (listener->ring_left > 0) {
            const unsigned char *bytes;
            size_t available = stream_read(ring, listener->position, &bytes);

            if (available == 0) {
                stats.dropped_lag++;

                listener_close(listener);

                return;
            }

            size_t wanted = listener->ring_left < available ? (size_t)listener->ring_left : available;
            ssize_t sent = send(listener->fd, bytes, wanted, MSG_NOSIGNAL);

            if (sent < 0) {
                if (errno == EAGAIN || errno == EWOULDBLOCK) {
                    listener_arm(listener, 1);
                }
                else if (errno != EINTR) {
                    listener_close(listener);
                }

                return;
            }

            listener->position += (uint64_t)sent;
            listener->ring_left -= (uint64_t)sent;
            listener->sent += (uint64_t)sent;
            stats.bytes_out += (uint64_t)sent;

            if (listener->icy) {
                listener->icy_left -= (size_t)sent;
            }

            continue;
        }

        if (listener->trailer_due) {
            listener->trailer_due = 0;

            listener_add(listener, "\r\n", 2);

            continue;
        }

        if (listener->icy && listener->icy_left == 0) {
            add_icy_block(listener);

            continue;
        }

        if (!stream_contains(ring, listener->position) || ring->head - listener->position > config.max_lag_bytes) {
            stats.dropped_lag++;

            listener_close(listener);

            return;
        }

        uint64_t available = ring->head - listener->position;

        if (available == 0) {
            listener_arm(listener, 0);

            return;
        }

        size_t unit = available < MAX_CHUNK ? (size_t)available : MAX_CHUNK;

        if (listener->icy && unit > listener->icy_left) {
            unit = listener->icy_left;
        }

        if (listener->chunked) {
            listener_print(listener, "%zx\r\n", unit);

            listener->trailer_due = 1;
        }

        listener->ring_left = unit;
    }
}

static void reply_with(Listener *listener, int status, const char *reason, const char *type, const char *extra, const char *body) {
    listener->mode = LISTENER_REPLY;

    listener_print(listener,
        "HTTP/1.1 %d %s\r\n"
        "Content-Type: %s\r\n"
        "Content-Length: %zu\r\n"
        "Cache-Control: no-store\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "%s"
        "Connection: close\r\n\r\n",
        status, reason, type, strlen(body), extra);

    listener_add(listener, body, strlen(body));

    listener_flush(listener);
}

static void reply(Listener *listener, int status, const char *reason, const char *type, const char *body) {
    reply_with(listener, status, reason, type, "", body);
}

static const char *upstream_state_name(const Upstream *upstream) {
    switch (upstream->state) {
        case UP_BODY: return "streaming";
        case UP_HEADERS: return "waiting for headers";
        case UP_CONNECTING: return "connecting";
        case UP_RESOLVING: return "resolving";
        default: return "waiting to retry";
    }
}

static uint64_t buffered_ms_of(const Stream *ring) {
    StreamMark first;

    if (!stream_frame_before(ring, stream_tail(ring), &first)) {
        return 0;
    }

    return (ring->time_ns - first.time_ns) / 1000000;
}

static void add_source_json(Text *text, const Mount *mount, int64_t now) {
    const Source *source = &mount->source;
    int connected = source->connection != NULL;

    text_add(text, "\"connected\":%s,\"on_air\":%s,\"method\":", connected ? "true" : "false", mount->on_air ? "true" : "false");
    text_json(text, connected ? source->method : "", 8);
    text_add(text, ",\"agent\":");
    text_json(text, connected ? source->agent : "", 100);
    text_add(text, ",\"name\":");
    text_json(text, connected ? source->name : "", 100);
    text_add(text, ",\"connected_s\":%lld,\"bytes_in\":%llu,\"connects\":%llu,\"drops\":%llu,\"last_drop\":",
        (long long)(connected ? (now - source->started_ms) / 1000 : 0), (unsigned long long)source->bytes,
        (unsigned long long)mount->connects, (unsigned long long)mount->drops);
    text_json(text, mount->last_drop, 63);
}

static void reply_stats(Listener *listener) {
    Text text;
    const StreamTitle *current = stream_title_at(&mp3.ring, mp3.ring.head);
    int64_t now = now_ms();

    text.length = 0;
    text.data[0] = '\0';

    text_add(&text,
        "{\"uptime_s\":%lld,\"listeners\":%d,\"event_listeners\":%d,\"peak_listeners\":%d,"
        "\"accepted\":%llu,\"rejected\":%llu,\"dropped_lag\":%llu,\"dropped_slow_request\":%llu,\"dropped_no_source\":%llu,"
        "\"resumed_exact\":%llu,\"resumed_fresh\":%llu,\"bytes_in\":%llu,\"bytes_out\":%llu,"
        "\"head\":%llu,\"tail\":%llu,\"buffered_ms\":%llu,\"stream_ms\":%llu,"
        "\"burst_bytes\":%zu,\"max_lag_bytes\":%zu,",
        (long long)((now - stats.started_ms) / 1000), mp3.listeners, stats.event_listeners, mp3.peak_listeners,
        (unsigned long long)stats.accepted, (unsigned long long)stats.rejected,
        (unsigned long long)stats.dropped_lag, (unsigned long long)stats.dropped_slow_request,
        (unsigned long long)stats.dropped_no_source,
        (unsigned long long)stats.resumed_exact, (unsigned long long)stats.resumed_fresh,
        (unsigned long long)audio.bytes, (unsigned long long)stats.bytes_out,
        (unsigned long long)mp3.ring.head, (unsigned long long)stream_tail(&mp3.ring),
        (unsigned long long)buffered_ms_of(&mp3.ring), (unsigned long long)(mp3.ring.time_ns / 1000000),
        config.burst_bytes, config.max_lag_bytes);

    text_add(&text,
        "\"source\":{\"state\":\"%s\",\"connects\":%llu,\"data_age_ms\":%lld,\"events_state\":\"%s\","
        "\"bitrate_kbps\":%d,\"sample_rate\":%d,\"channels\":%d,\"frames\":%llu,\"dropped_bytes\":%llu,"
        "\"tag_bytes\":%llu,\"info_frames\":%llu,\"resyncs\":%llu,\"format_changes\":%llu},",
        upstream_state_name(&audio), (unsigned long long)audio.connects,
        (long long)(audio.last_data_ms ? now - audio.last_data_ms : -1), upstream_state_name(&events),
        framer.format.bitrate_kbps, framer.format.sample_rate, framer.format.channels,
        (unsigned long long)framer.frames, (unsigned long long)framer.dropped_bytes,
        (unsigned long long)framer.tag_bytes, (unsigned long long)framer.info_frames,
        (unsigned long long)framer.resyncs, (unsigned long long)framer.format_changes);

    text_add(&text, "\"on_air\":{\"artist\":");
    text_json(&text, current ? current->artist : "", 160);
    text_add(&text, ",\"title\":");
    text_json(&text, current ? current->title : "", 160);
    text_add(&text, "},\"live\":{");
    add_source_json(&text, &mp3, now);
    text_add(&text,
        ",\"frames\":%llu,\"dropped_bytes\":%llu,\"format_changes\":%llu,\"bitrate_kbps\":%d,\"sample_rate\":%d},",
        (unsigned long long)live_framer.frames, (unsigned long long)live_framer.dropped_bytes,
        (unsigned long long)live_framer.format_changes, live_framer.format.bitrate_kbps, live_framer.format.sample_rate);
    text_add(&text, "\"opus\":{");
    add_source_json(&text, &opus, now);
    text_add(&text,
        ",\"listeners\":%d,\"peak_listeners\":%d,\"head\":%llu,\"tail\":%llu,\"buffered_ms\":%llu,\"stream_ms\":%llu,"
        "\"pages\":%llu,\"crc_errors\":%llu,\"dropped_bytes\":%llu,\"chains\":%llu,\"skipped_pages\":%llu,"
        "\"resets\":%llu,\"channels\":%d,\"pre_skip\":%d,\"follows_mp3_titles\":%s},",
        opus.listeners, opus.peak_listeners,
        (unsigned long long)opus.ring.head, (unsigned long long)stream_tail(&opus.ring),
        (unsigned long long)buffered_ms_of(&opus.ring), (unsigned long long)(opus.ring.time_ns / 1000000),
        (unsigned long long)splicer.pages_out, (unsigned long long)ogg_reader.crc_errors,
        (unsigned long long)ogg_reader.dropped_bytes, (unsigned long long)splicer.chains,
        (unsigned long long)splicer.skipped_pages, (unsigned long long)splicer.resets,
        splicer.head.channels, splicer.head.pre_skip, opus.follows ? "true" : "false");
    text_add(&text,
        "\"moq\":{\"connections\":%d,\"peak_connections\":%d,\"accepted\":%llu,\"refused\":%llu,\"skips\":%llu,"
        "\"objects_out\":%llu,\"too_big\":%llu,\"group\":%llu,\"objects\":%llu,\"meta_objects\":%llu,"
        "\"catalog_version\":%llu,\"packets_oversized\":%llu,\"edge_samples\":%llu,"
        "\"lite_connections\":%d,\"lite_peak\":%d,\"lite_accepted\":%llu,\"lite_failures\":%llu},",
        stats.moq_connections, stats.moq_peak, (unsigned long long)stats.moq_accepted,
        (unsigned long long)stats.moq_refused, (unsigned long long)stats.moq_skips,
        (unsigned long long)stats.moq_objects_out, (unsigned long long)stats.moq_too_big,
        (unsigned long long)audio_track.group, (unsigned long long)audio_track.count,
        (unsigned long long)meta_track.count, (unsigned long long)catalog_version,
        (unsigned long long)splicer.oversized_packets, (unsigned long long)audio_clock_end,
        stats.lite_connections, stats.lite_peak, (unsigned long long)stats.lite_accepted,
        (unsigned long long)stats.lite_failures);
    QuicStats quic;

    quic_stats(&quic);

    text_add(&text,
        "\"quic\":{\"enabled\":%s,\"port\":%d,\"protocol\":\"%s\",\"sessions\":%d,\"peak_sessions\":%d,\"accepted\":%llu,"
        "\"bare_accepted\":%llu,\"refused\":%llu,\"failures\":%llu,\"closed\":%llu,\"packets_in\":%llu,\"packets_out\":%llu,"
        "\"groups_out\":%llu,\"frames_out\":%llu,\"skips\":%llu,\"certificate_valid_ms\":%lld},",
        quic.enabled ? "true" : "false", quic.port, QUIC_PROTOCOL, quic.sessions, quic.peak,
        (unsigned long long)quic.accepted, (unsigned long long)quic.bare_accepted,
        (unsigned long long)quic.refused, (unsigned long long)quic.failures,
        (unsigned long long)quic.closed, (unsigned long long)quic.packets_in, (unsigned long long)quic.packets_out,
        (unsigned long long)quic.groups_out, (unsigned long long)quic.frames_out, (unsigned long long)quic.skips,
        (long long)quic.certificate_valid_ms);
    text_add(&text,
        "\"sources\":{\"enabled\":%s,\"unauthorized\":%llu,\"refused\":%llu,\"metadata_updates\":%llu}}\n",
        config.source_password[0] ? "true" : "false", (unsigned long long)stats.sources_unauthorized,
        (unsigned long long)stats.sources_refused, (unsigned long long)stats.metadata_updates);

    reply(listener, 200, "OK", "application/json", text.data);
}

static void reply_now(Listener *listener, const Mount *mount) {
    Listener scratch;

    memset(&scratch, 0, sizeof(scratch));

    listener_print(&scratch, "{\"on_air\":");
    add_title_json(&scratch, title_at_head(mount));
    listener_print(&scratch, ",\"audible\":");
    add_title_json(&scratch, audible_title_of(mount));

    if (mount == &mp3) {
        QuicStats quic;

        quic_stats(&quic);
        listener_print(&scratch, ",\"listeners\":%d,\"source\":\"%s\",\"opus\":%s}\n",
            mp3.listeners + opus.listeners + stats.moq_connections + quic.sessions,
            mp3.on_air ? "live" : audio.state == UP_BODY ? "radio" : "none",
            opus_available() ? "true" : "false");
    }
    else {
        listener_print(&scratch, ",\"listeners\":%d,\"source\":\"%s\"}\n", opus.listeners, opus_available() ? "live" : "none");
    }

    scratch.out[scratch.out_length < OUT_MAX ? scratch.out_length : OUT_MAX - 1] = '\0';

    reply(listener, 200, "OK", "application/json", (const char *)scratch.out);
}

static void send_event(Listener *listener, const StreamTitle *title) {
    if (listener->out_length > 0) {
        return;
    }

    listener_print(listener, "data: ");
    add_title_json(listener, title);
    listener_print(listener, "\n\n");

    listener->event_title_id = title ? title->id : 0;
    listener->last_write_ms = now_ms();

    listener_flush(listener);
}

static void start_events(Listener *listener, Mount *mount) {
    listener->mode = LISTENER_EVENTS;
    listener->mount = mount;

    stats.event_listeners++;

    listener_print(listener,
        "HTTP/1.1 200 OK\r\n"
        "Content-Type: text/event-stream\r\n"
        "Cache-Control: no-cache\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "X-Accel-Buffering: no\r\n"
        "Connection: close\r\n\r\n");

    const StreamTitle *title = audible_title_of(mount);

    if (title) {
        listener_print(listener, "data: ");
        add_title_json(listener, title);
        listener_print(listener, "\n\n");

        listener->event_title_id = title->id;
    }
    else {
        listener_print(listener, ": waiting for the first title\n\n");
    }

    listener->last_write_ms = now_ms();

    listener_flush(listener);
}

static size_t opus_header_pages(unsigned char *out, size_t capacity) {
    const StreamTitle *current = audible_title_of(&opus);
    OpusTitle title;

    memset(&title, 0, sizeof(title));

    if (current) {
        copy_utf8(title.artist, sizeof(title.artist), current->artist, strlen(current->artist));
        copy_utf8(title.album, sizeof(title.album), current->album, strlen(current->album));
        copy_utf8(title.title, sizeof(title.title), current->title, strlen(current->title));
    }

    return opus_splicer_headers(&splicer, &title, out, capacity);
}

static void start_stream(Listener *listener, const HttpRequest *request, int head_only, Mount *mount) {
    if (mount == &opus && !opus_available()) {
        reply_with(listener, 503, "Service Unavailable", "text/plain", "Retry-After: 5\r\n", "no source on this mount\n");

        return;
    }

    Stream *ring = &mount->ring;
    int64_t now = now_ms();
    uint64_t offset = request->has_range ? request->range_start : 0;
    uint64_t start = stream_start_position(ring, config.burst_bytes);
    int keyed = request->has_query && !request->wants_icy;
    uint64_t key = keyed ? hash_text(request->target) : 0;
    unsigned char pages[2048];
    size_t page_bytes = 0;

    if (keyed && offset > 0 && !head_only) {
        Session *session = session_find(key, now);
        uint64_t wanted = session ? session->base + offset : 0;

        if (session && stream_contains(ring, wanted) && ring->head - wanted <= config.max_lag_bytes) {
            start = wanted;
            stats.resumed_exact++;
        }
        else {
            stats.resumed_fresh++;
        }
    }
    else if (offset > 0 && !head_only) {
        stats.resumed_fresh++;
    }

    if (mount == &opus && offset == 0 && !head_only) {
        page_bytes = opus_header_pages(pages, sizeof(pages));
    }

    if (keyed && !head_only) {
        session_store(key, start - offset - page_bytes, now);
    }

    listener->mount = mount;
    listener->chunked = request->version_minor >= 1 && !head_only;
    listener->icy = request->wants_icy && !head_only && mount == &mp3;
    listener->icy_left = (size_t)config.icy_metaint;
    listener->position = start;

    listener_print(listener,
        "HTTP/1.1 %s\r\n"
        "Content-Type: %s\r\n"
        "Cache-Control: no-cache, no-store\r\n"
        "Access-Control-Allow-Origin: *\r\n",
        offset > 0 ? "206 Partial Content" : "200 OK", mount->type);

    if (offset > 0) {
        listener_print(listener,
            "Accept-Ranges: bytes\r\nContent-Range: bytes %llu-9007199254740990/*\r\n", (unsigned long long)offset);
    }

    if (listener->icy) {
        listener_print(listener,
            "icy-name: %s\r\nicy-br: %d\r\nicy-metaint: %d\r\n",
            config.station, framer.format.bitrate_kbps ? framer.format.bitrate_kbps : 128, config.icy_metaint);
    }

    listener_print(listener, listener->chunked ? "Transfer-Encoding: chunked\r\n\r\n" : "Connection: close\r\n\r\n");

    if (page_bytes > 0) {
        if (listener->chunked) {
            listener_print(listener, "%zx\r\n", page_bytes);
        }

        listener_add(listener, pages, page_bytes);

        if (listener->chunked) {
            listener_add(listener, "\r\n", 2);
        }
    }

    if (head_only) {
        listener->mode = LISTENER_REPLY;
    }
    else {
        listener->mode = LISTENER_STREAM;

        mount->listeners++;

        if (mount->listeners > mount->peak_listeners) {
            mount->peak_listeners = mount->listeners;
        }
    }

    listener_flush(listener);
}

static void catalog_update(void);

static void source_lost(Mount *mount, const char *why) {
    Source *source = &mount->source;

    if (!source->connection) {
        return;
    }

    say("the source left %s: %s, after %llu bytes in %lld s", mount->path, why,
        (unsigned long long)source->bytes, (long long)((now_ms() - source->started_ms) / 1000));

    snprintf(mount->last_drop, sizeof(mount->last_drop), "%s", why);

    source->connection = NULL;
    mount->drops++;
    mount->lost_ms = now_ms();
    mount->has_live_title = 0;

    if (mount == &mp3) {
        int was_on_air = mp3.on_air;

        mp3.on_air = 0;

        mp3_framer_reset(&live_framer);

        if (was_on_air) {
            say("back on the radio at %s", mp3.path);

            if (have_radio_title) {
                stream_set_title(&mp3.ring, radio_title.artist, radio_title.album, radio_title.title, radio_title.is_request);
            }
        }
    }
    else {
        opus.on_air = 0;
        opus.follows = 1;

        ogg_reader_reset(&ogg_reader);
        opus_splicer_source_ended(&splicer);
        catalog_update();
    }
}

static void source_timed(Mount *mount, uint64_t duration_ns) {
    Source *source = &mount->source;
    int64_t now = now_ms();

    source->media_ns += duration_ns;
    source->window_ns += duration_ns;

    int64_t wall = now - source->window_ms;
    int64_t ahead = (int64_t)(source->window_ns / 1000000) - wall;

    if (ahead > (int64_t)config.source_max_ahead * 1000) {
        source->kill = "sends faster than real time";

        return;
    }

    if (wall > 600000) {
        source->window_ms = now;
        source->window_ns = ahead > 0 ? (uint64_t)ahead * 1000000 : 0;
    }
}

static void on_live_frame(void *context, const unsigned char *frame, size_t length, const Mp3Header *header) {
    (void)context;

    if (!mp3.source.connection || mp3.source.kill) {
        return;
    }

    uint64_t duration = (uint64_t)header->samples * 1000000000ULL / (uint64_t)header->sample_rate;

    if (!mp3.on_air) {
        mp3.on_air = 1;

        if (mp3.has_live_title) {
            stream_set_title(&mp3.ring, mp3.live_title.artist, mp3.live_title.album, mp3.live_title.title, 0);
        }
        else {
            stream_set_title(&mp3.ring, "", "", mp3.source.name[0] ? mp3.source.name : "Live", 0);
        }

        say("the source is on air at %s", mp3.path);
    }

    stream_append(&mp3.ring, frame, length, duration);
    source_timed(&mp3, duration);
}

static void on_ogg_page(void *context, const OggPage *page) {
    (void)context;

    opus_splicer_page(&splicer, page);
}

static void on_opus_page(void *context, const unsigned char *page, size_t length, uint64_t samples) {
    (void)context;

    if (!opus.source.connection || opus.source.kill) {
        return;
    }

    uint64_t duration = samples * 1000000000ULL / OPUS_RATE;

    if (!opus.on_air) {
        opus.on_air = 1;

        say("the source is on air at %s with %d channels", opus.path, splicer.head.channels);
        catalog_update();
    }

    stream_append(&opus.ring, page, length, duration);
    source_timed(&opus, duration);
}

static void on_opus_title(void *context, const OpusTitle *title) {
    (void)context;

    if (title->title[0] == '\0' && title->artist[0] == '\0') {
        opus.follows = 1;

        return;
    }

    opus.follows = 0;

    stream_set_title(&opus.ring, title->artist, title->album, title->title, 0);

    say("now playing at %s: %s - %s", opus.path, title->artist, title->title);
}

static void on_opus_reset(void *context) {
    (void)context;

    for (int index = 0; index < config.max_listeners; index++) {
        Listener *listener = listeners[index];

        if (listener && listener->mode == LISTENER_STREAM && listener->mount == &opus) {
            listener->expired = 1;
        }
    }

    stream_clear(&opus.ring);
    object_track_clear(&audio_track);
    catalog_update();

    say("the source changed the channel layout at %s, its listeners start again", opus.path);
}

static void source_feed(void *context, const unsigned char *data, size_t length) {
    Listener *listener = context;

    if (listener->mount == &mp3) {
        mp3_framer_feed(&live_framer, data, length);
    }
    else {
        ogg_reader_feed(&ogg_reader, data, length);
    }
}

static int source_data(Listener *listener, const unsigned char *data, size_t length) {
    Mount *mount = listener->mount;
    Source *source = &mount->source;
    const char *finished = NULL;

    source->bytes += length;
    source->last_data_ms = now_ms();

    if (listener->body_chunked) {
        int result = chunk_decoder_feed(&listener->body_decoder, data, length, source_feed, listener);

        if (result > 0) {
            finished = "finished the upload";
        }
        else if (result < 0) {
            finished = "sent broken chunks";
        }
    }
    else {
        if (listener->body_left >= 0 && (int64_t)length > listener->body_left) {
            length = (size_t)listener->body_left;
        }

        source_feed(listener, data, length);

        if (listener->body_left >= 0) {
            listener->body_left -= (int64_t)length;

            if (listener->body_left == 0) {
                finished = "finished the upload";
            }
        }
    }

    if (!finished && source->kill) {
        finished = source->kill;
    }

    if (finished) {
        source_lost(mount, finished);
        listener_close(listener);

        return -1;
    }

    return 0;
}

static void source_readable(Listener *listener) {
    unsigned char buffer[16384];

    for (int round = 0; round < 8; round++) {
        ssize_t got = recv(listener->fd, buffer, sizeof(buffer), 0);

        if (got < 0) {
            if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
                source_lost(listener->mount, "broke the connection");
                listener_close(listener);
            }

            return;
        }

        if (got == 0) {
            source_lost(listener->mount, "disconnected");
            listener_close(listener);

            return;
        }

        if (source_data(listener, buffer, (size_t)got) < 0) {
            return;
        }
    }
}

static int header_contains(const char *headers, const char *name, const char *needle) {
    char value[256];

    return http_header_value(headers, name, value, sizeof(value)) && strcasestr(value, needle) != NULL;
}

static int source_authorized(const char *headers) {
    char value[512];
    char user[64];
    char password[256];

    return config.source_password[0] != '\0' &&
        http_header_value(headers, "Authorization", value, sizeof(value)) &&
        http_basic_credentials(value, user, sizeof(user), password, sizeof(password)) &&
        strcmp(user, config.source_user) == 0 &&
        text_equal_secret(password, config.source_password);
}

static void reply_unauthorized(Listener *listener) {
    stats.sources_unauthorized++;

    reply_with(listener, 401, "Unauthorized", "text/plain", "WWW-Authenticate: Basic realm=\"caligo-relay\"\r\n",
        "wrong source credentials\n");
}

static int source_type_fits(const Mount *mount, const char *type) {
    if (mount == &mp3) {
        return strcasestr(type, "mpeg") != NULL || strcasestr(type, "mp3") != NULL;
    }

    return strcasestr(type, "ogg") != NULL || strcasestr(type, "opus") != NULL;
}

static void accept_source(
    Listener *listener, const HttpRequest *request, const char *headers, const unsigned char *body, size_t body_length) {
    Mount *mount = mount_named(relay_path(request->path));
    char value[256];

    if (config.source_password[0] == '\0') {
        stats.sources_refused++;

        reply(listener, 403, "Forbidden", "text/plain", "sources are switched off\n");

        return;
    }

    if (!source_authorized(headers)) {
        say("refused a source for %s: wrong credentials", mount ? mount->path : "an unknown mount");

        reply_unauthorized(listener);

        return;
    }

    if (!mount) {
        stats.sources_refused++;

        reply(listener, 404, "Not Found", "text/plain", "no such mount, use /live/ or /live/opus\n");

        return;
    }

    if (mount->source.connection) {
        stats.sources_refused++;

        say("refused a second source for %s", mount->path);
        reply(listener, 403, "Forbidden", "text/plain", "mountpoint in use\n");

        return;
    }

    if (http_header_value(headers, "Content-Type", value, sizeof(value)) && !source_type_fits(mount, value)) {
        stats.sources_refused++;

        reply(listener, 415, "Unsupported Media Type", "text/plain",
            mount == &mp3 ? "this mount takes audio/mpeg\n" : "this mount takes Ogg Opus\n");

        return;
    }

    listener->body_left = -1;
    listener->body_chunked = header_contains(headers, "Transfer-Encoding", "chunked");

    chunk_decoder_init(&listener->body_decoder);

    if (!listener->body_chunked && http_header_value(headers, "Content-Length", value, sizeof(value))) {
        char *end = NULL;
        long long length = strtoll(value, &end, 10);

        if (end == value || *end != '\0' || length <= 0) {
            reply(listener, 400, "Bad Request", "text/plain", "bad Content-Length\n");

            return;
        }

        listener->body_left = length;
    }

    Source *source = &mount->source;
    int64_t now = now_ms();
    int slot = listener->slot;

    memset(source, 0, sizeof(*source));

    source->connection = listener;
    source->started_ms = now;
    source->last_data_ms = now;
    source->window_ms = now;

    snprintf(source->method, sizeof(source->method), "%s", request->method);

    if (http_header_value(headers, "User-Agent", value, sizeof(value))) {
        text_utf8_clean(value, source->agent, sizeof(source->agent));
    }

    if (http_header_value(headers, "ice-name", value, sizeof(value))) {
        text_utf8_clean(value, source->name, sizeof(source->name));
    }

    listener->mode = LISTENER_SOURCE;
    listener->mount = mount;
    mount->connects++;
    mount->on_air = 0;
    mount->has_live_title = 0;

    if (mount == &mp3) {
        mp3_framer_reset(&live_framer);
    }
    else {
        ogg_reader_reset(&ogg_reader);
        opus_splicer_source_ended(&splicer);
    }

    say("a source connected to %s with %s from %s", mount->path, source->method,
        source->agent[0] ? source->agent : "an unnamed client");

    if (strcmp(request->method, "SOURCE") == 0) {
        listener_print(listener, "HTTP/1.0 200 OK\r\nServer: caligo-relay\r\n\r\n");
    }
    else if (header_contains(headers, "Expect", "100-continue")) {
        listener_print(listener, "HTTP/1.1 100 Continue\r\n\r\n");
    }
    else {
        listener_print(listener, "HTTP/1.1 200 OK\r\nServer: caligo-relay\r\nCache-Control: no-store\r\n\r\n");
    }

    listener_flush(listener);

    if (listeners[slot] == listener && body_length > 0) {
        source_data(listener, body, body_length);
    }
}

static void split_song(const char *song, OpusTitle *title) {
    const char *dash = strstr(song, " - ");

    if (dash) {
        copy_utf8(title->artist, sizeof(title->artist), song, (size_t)(dash - song));
        copy_utf8(title->title, sizeof(title->title), dash + 3, strlen(dash + 3));
    }
    else {
        copy_utf8(title->title, sizeof(title->title), song, strlen(song));
    }
}

static void admin_metadata(Listener *listener, const HttpRequest *request, const char *headers) {
    char mode[32];
    char name[128];
    char normalized[160];
    char raw[512];
    char clean[512];
    OpusTitle title;

    memset(&title, 0, sizeof(title));

    if (!source_authorized(headers)) {
        reply_unauthorized(listener);

        return;
    }

    if (!http_query_value(request->target, "mode", mode, sizeof(mode)) || strcmp(mode, "updinfo") != 0) {
        reply(listener, 400, "Bad Request", "text/plain", "mode must be updinfo\n");

        return;
    }

    if (!http_query_value(request->target, "mount", name, sizeof(name))) {
        reply(listener, 400, "Bad Request", "text/plain", "mount is missing\n");

        return;
    }

    snprintf(normalized, sizeof(normalized), "%s%s", name[0] == '/' ? "" : "/", name);

    Mount *mount = mount_named(relay_path(normalized));

    if (!mount) {
        reply(listener, 404, "Not Found", "text/plain", "no such mount\n");

        return;
    }

    if (!mount->source.connection) {
        reply(listener, 400, "Bad Request", "text/plain", "no source on this mount\n");

        return;
    }

    if (http_query_value(request->target, "title", raw, sizeof(raw))) {
        text_utf8_clean(raw, title.title, sizeof(title.title));

        if (http_query_value(request->target, "artist", raw, sizeof(raw))) {
            text_utf8_clean(raw, title.artist, sizeof(title.artist));
        }
    }
    else if (http_query_value(request->target, "song", raw, sizeof(raw))) {
        text_utf8_clean(raw, clean, sizeof(clean));
        split_song(clean, &title);
    }
    else {
        reply(listener, 400, "Bad Request", "text/plain", "song or title is missing\n");

        return;
    }

    if (http_query_value(request->target, "album", raw, sizeof(raw))) {
        text_utf8_clean(raw, title.album, sizeof(title.album));
    }

    stats.metadata_updates++;

    if (mount == &mp3) {
        mp3.live_title = title;
        mp3.has_live_title = 1;

        if (mp3.on_air) {
            stream_set_title(&mp3.ring, title.artist, title.album, title.title, 0);
        }
    }
    else {
        opus.follows = 0;

        stream_set_title(&opus.ring, title.artist, title.album, title.title, 0);
    }

    say("now playing at %s: %s%s%s", mount->path, title.artist, title.artist[0] ? " - " : "", title.title);

    reply(listener, 200, "OK", "text/xml", METADATA_DONE);
}

static void catalog_update(void) {
    char next[sizeof(catalog_json)];
    int written = snprintf(next, sizeof(next),
        "{\"version\":0,\"broadcast\":\"caligo/radio/main\",\"on_air\":%s,"
        "\"tracks\":[{\"id\":%d,\"name\":\"audio/hi\",\"codec\":\"opus\",\"timescale\":%d,\"sample_rate\":%d,"
        "\"channels\":%d,\"group_ms\":%d},"
        "{\"id\":%d,\"name\":\"meta\",\"type\":\"application/json\"}]}",
        opus_available() ? "true" : "false", TRACK_AUDIO, OPUS_RATE, OPUS_RATE,
        splicer.have_head ? splicer.head.channels : 2, config.moq_group_ms, TRACK_META);

    if (written <= 0 || (size_t)written >= sizeof(next)) {
        return;
    }

    if ((size_t)written == catalog_length && memcmp(next, catalog_json, catalog_length) == 0) {
        return;
    }

    memcpy(catalog_json, next, (size_t)written + 1);

    catalog_length = (size_t)written;
    catalog_version++;
}

static void meta_update(void) {
    const StreamTitle *title = title_at_head(&opus);
    char artist[STREAM_TEXT * 2];
    char album[STREAM_TEXT * 2];
    char name[STREAM_TEXT * 2];

    if (!title || title->id == moq_title_id) {
        return;
    }

    moq_title_id = title->id;

    json_escape(artist, sizeof(artist), title->artist);
    json_escape(album, sizeof(album), title->album);
    json_escape(name, sizeof(name), title->title);

    int written = snprintf((char *)object_buffer, sizeof(object_buffer),
        "{\"at\":%llu,\"artist\":\"%s\",\"album\":\"%s\",\"title\":\"%s\",\"is_request\":%s}",
        (unsigned long long)audio_clock_end, artist, album, name, title->is_request ? "true" : "false");

    if (written > 0 && (size_t)written < sizeof(object_buffer)) {
        object_track_append(&meta_track, object_buffer, (size_t)written, audio_clock_end, 1);
    }
}

static void on_opus_packet(void *context, const unsigned char *packet, size_t length, uint64_t start, int samples) {
    (void)context;

    if (!opus.source.connection || opus.source.kill || length + 16 > sizeof(object_buffer)) {
        return;
    }

    size_t header = 0;

    object_buffer[header++] = 0;
    header += varint_encode(start, object_buffer + header);

    memcpy(object_buffer + header, packet, length);
    object_track_append(&audio_track, object_buffer, header + length, start, 0);

    audio_clock_end = start + (uint64_t)samples;
}

static int moq_add(Listener *listener, int track, uint64_t group, uint64_t index, const unsigned char *payload, size_t length) {
    unsigned char header[1 + 8 * 3];
    unsigned char frame[10];
    size_t header_length = 0;

    header[header_length++] = MOQ_OBJECT;
    header_length += varint_encode((uint64_t)track, header + header_length);
    header_length += varint_encode(group, header + header_length);
    header_length += varint_encode(index, header + header_length);

    size_t frame_length = ws_frame_header(frame, WS_BINARY, header_length + length);
    size_t total = frame_length + header_length + length;

    if (total > OUT_MAX) {
        stats.moq_too_big++;

        return -1;
    }

    if (total > listener_room(listener)) {
        return 0;
    }

    listener_add(listener, frame, frame_length);
    listener_add(listener, header, header_length);
    listener_add(listener, payload, length);

    return 1;
}

static int moq_next(Listener *listener) {
    int added = 0;

    for (;;) {
        int result;

        if (listener->catalog_sent != catalog_version) {
            result = moq_add(listener, TRACK_CATALOG, catalog_version, 0, (const unsigned char *)catalog_json, catalog_length);

            if (result == 0) {
                return added;
            }

            listener->catalog_sent = catalog_version;
            added |= result > 0;

            continue;
        }

        uint64_t meta_oldest = object_track_oldest(&meta_track);

        if (listener->meta_next < meta_oldest) {
            listener->meta_next = meta_oldest;
        }

        if (listener->meta_next < meta_track.count) {
            const ObjectEntry *entry = object_track_entry(&meta_track, listener->meta_next);
            size_t length = entry ? object_track_copy(&meta_track, entry, object_buffer, sizeof(object_buffer)) : 0;

            if (length > 0) {
                result = moq_add(listener, TRACK_META, entry->group, entry->index, object_buffer, length);

                if (result == 0) {
                    return added;
                }

                added |= result > 0;
            }

            listener->meta_next++;

            continue;
        }

        if (listener->audio_next >= audio_track.count) {
            return added;
        }

        uint64_t oldest = object_track_oldest(&audio_track);
        const ObjectEntry *entry = listener->audio_next >= oldest ? object_track_entry(&audio_track, listener->audio_next) : NULL;

        if (!entry || entry->group + 1 < audio_track.group) {
            uint64_t latest = audio_track.group_first > oldest ? audio_track.group_first : oldest;

            if (latest <= listener->audio_next) {
                return added;
            }

            listener->audio_next = latest;
            stats.moq_skips++;

            continue;
        }

        size_t length = object_track_copy(&audio_track, entry, object_buffer, sizeof(object_buffer));

        result = length > 0 ? moq_add(listener, TRACK_AUDIO, entry->group, entry->index, object_buffer, length) : -1;

        if (result == 0) {
            return added;
        }

        listener->audio_next++;

        if (result > 0) {
            stats.moq_objects_out++;
            added = 1;
        }
    }
}

static void moq_close(Listener *listener, int code) {
    unsigned char frame[4];
    unsigned char payload[2] = { (unsigned char)(code >> 8), (unsigned char)code };
    size_t length = ws_frame_header(frame, WS_CLOSE, sizeof(payload));

    if (listener_room(listener) >= length + sizeof(payload)) {
        listener_add(listener, frame, length);
        listener_add(listener, payload, sizeof(payload));
    }

    stats.moq_connections--;
    listener->mode = LISTENER_REPLY;

    listener_flush(listener);
}

static void moq_readable(Listener *listener) {
    if (listener->request_length >= REQUEST_MAX) {
        moq_close(listener, 1009);

        return;
    }

    ssize_t got = recv(listener->fd, listener->request + listener->request_length, REQUEST_MAX - listener->request_length, 0);

    if (got == 0 || (got < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
        listener_close(listener);

        return;
    }

    if (got < 0) {
        return;
    }

    listener->request_length += (size_t)got;
    listener->last_seen_ms = now_ms();

    size_t position = 0;

    while (position < listener->request_length) {
        WsFrame frame;
        long used = ws_parse_frame(
            (unsigned char *)listener->request + position, listener->request_length - position, 1024, &frame);

        if (used == 0) {
            break;
        }

        if (used < 0) {
            moq_close(listener, used == WS_ERROR_TOO_LARGE ? 1009 : 1002);

            return;
        }

        position += (size_t)used;

        if (frame.opcode == WS_CLOSE) {
            moq_close(listener, 1000);

            return;
        }

        if (frame.opcode == WS_PING) {
            unsigned char header[4];
            size_t length = ws_frame_header(header, WS_PONG, frame.length);

            if (listener_room(listener) >= length + frame.length) {
                listener_add(listener, header, length);
                listener_add(listener, frame.payload, (size_t)frame.length);
            }
        }
        else if (frame.opcode == WS_BINARY && frame.length >= 1 && frame.length <= 17 && frame.payload[0] == MOQ_PROBE) {
            unsigned char answer[17 + 8];
            unsigned char header[2];
            size_t answer_length = (size_t)frame.length;

            memcpy(answer, frame.payload, answer_length);

            answer_length += varint_encode(audio_clock_end, answer + answer_length);

            size_t length = ws_frame_header(header, WS_BINARY, answer_length);

            if (listener_room(listener) >= length + answer_length) {
                listener_add(listener, header, length);
                listener_add(listener, answer, answer_length);
            }
        }
    }

    memmove(listener->request, listener->request + position, listener->request_length - position);

    listener->request_length -= position;

    listener_flush(listener);
}

static void lite_readable(Listener *listener) {
    MoqlSession *session = listener->moql;
    int64_t now = now_ms();

    if (listener->ws_in_length >= LITE_INPUT) {
        stats.lite_failures++;

        listener_close(listener);

        return;
    }

    ssize_t got = recv(listener->fd, listener->ws_in + listener->ws_in_length, LITE_INPUT - listener->ws_in_length, 0);

    if (got == 0 || (got < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
        listener_close(listener);

        return;
    }

    if (got < 0) {
        return;
    }

    listener->ws_in_length += (size_t)got;
    listener->last_seen_ms = now;

    size_t position = 0;

    while (position < listener->ws_in_length && !session->closing) {
        WsFrame frame;
        long used = ws_parse_frame(listener->ws_in + position, listener->ws_in_length - position, MOQL_RECORD, &frame);

        if (used == 0) {
            break;
        }

        if (used < 0 || frame.opcode == WS_TEXT || frame.opcode == WS_CONTINUATION ||
            (frame.opcode == WS_BINARY && !frame.fin)) {
            stats.lite_failures++;

            moql_close(session, 0x3, "bad websocket frame");

            break;
        }

        position += (size_t)used;

        if (frame.opcode == WS_BINARY && moql_receive(session, frame.payload, (size_t)frame.length, now) < 0) {
            stats.lite_failures++;

            say("closed a moq-lite session: %s", session->error ? session->error : "protocol violation");
            moql_close(session, 0x3, session->error ? session->error : "protocol violation");

            break;
        }

        if (frame.opcode == WS_PING) {
            unsigned char header[4];

            moql_raw(session, header, ws_frame_header(header, WS_PONG, frame.length));
            moql_raw(session, frame.payload, (size_t)frame.length);
        }
        else if (frame.opcode == WS_CLOSE) {
            session->closing = 1;
        }
    }

    memmove(listener->ws_in, listener->ws_in + position, listener->ws_in_length - position);

    listener->ws_in_length -= position;

    listener_flush(listener);
}

static void accept_websocket(Listener *listener, const HttpRequest *request, const char *headers) {
    char value[256];
    char key[64];
    char accept[32];

    if (strcmp(request->method, "GET") != 0 || request->version_minor < 1) {
        reply(listener, 400, "Bad Request", "text/plain", "a WebSocket needs GET over HTTP/1.1\n");

        return;
    }

    if (!http_header_value(headers, "Upgrade", value, sizeof(value)) || !ws_has_token(value, "websocket") ||
        !http_header_value(headers, "Connection", value, sizeof(value)) || !ws_has_token(value, "upgrade") ||
        !http_header_value(headers, "Sec-WebSocket-Version", value, sizeof(value)) || strcmp(value, "13") != 0) {
        stats.moq_refused++;

        reply_with(listener, 426, "Upgrade Required", "text/plain", "Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n",
            "this address takes a WebSocket\n");

        return;
    }

    if (!http_header_value(headers, "Sec-WebSocket-Key", key, sizeof(key)) || !ws_accept_key(key, accept, sizeof(accept))) {
        stats.moq_refused++;

        reply(listener, 400, "Bad Request", "text/plain", "bad Sec-WebSocket-Key\n");

        return;
    }

    char offered[2048];
    const char *chosen = NULL;
    int qmux = 0;

    if (http_header_value(headers, "Sec-WebSocket-Protocol", offered, sizeof(offered))) {
        if (ws_offers_protocol(offered, MOQ_PROTOCOL)) {
            chosen = MOQ_PROTOCOL;
        }
        else if (ws_offers_protocol(offered, LITE_PROTOCOL_02)) {
            chosen = LITE_PROTOCOL_02;
            qmux = 2;
        }
        else if (ws_offers_protocol(offered, LITE_PROTOCOL_01)) {
            chosen = LITE_PROTOCOL_01;
            qmux = 1;
        }
    }

    if (!chosen) {
        stats.moq_refused++;

        reply(listener, 400, "Bad Request", "text/plain",
            "offer the " MOQ_PROTOCOL " or " LITE_PROTOCOL_02 " subprotocol\n");

        return;
    }

    int64_t now = now_ms();
    MoqlSession *session = NULL;
    unsigned char *input = NULL;

    if (qmux) {
        session = moql_create(&lite_source, qmux, lite_hop, now);
        input = malloc(LITE_INPUT);

        if (!session || !input) {
            moql_free(session);
            free(input);
            reply(listener, 503, "Service Unavailable", "text/plain", "out of memory\n");

            return;
        }
    }

    listener_print(listener,
        "HTTP/1.1 101 Switching Protocols\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        "Sec-WebSocket-Accept: %s\r\n"
        "Sec-WebSocket-Protocol: %s\r\n\r\n",
        accept, chosen);

    if (qmux) {
        listener->mode = LISTENER_MOQL;
        listener->moql = session;
        listener->ws_in = input;
        listener->ws_in_length = 0;
        listener->last_seen_ms = now;

        stats.lite_accepted++;
        stats.lite_connections++;

        if (stats.lite_connections > stats.lite_peak) {
            stats.lite_peak = stats.lite_connections;
        }

        listener_flush(listener);

        return;
    }

    if (config.moq_send_buffer > 0) {
        setsockopt(listener->fd, SOL_SOCKET, SO_SNDBUF, &config.moq_send_buffer, sizeof(config.moq_send_buffer));
    }

    listener->mode = LISTENER_MOQ;
    listener->request_length = 0;
    listener->audio_next = opus_available() && audio_track.count > audio_track.floor ? audio_track.group_first : audio_track.count;
    listener->meta_next = meta_track.count > meta_track.floor ? meta_track.count - 1 : meta_track.count;
    listener->catalog_sent = 0;
    listener->last_seen_ms = now;
    listener->last_ping_ms = now;

    stats.moq_accepted++;
    stats.moq_connections++;

    if (stats.moq_connections > stats.moq_peak) {
        stats.moq_peak = stats.moq_connections;
    }

    listener_flush(listener);
}

static void route(Listener *listener, const char *headers, const unsigned char *body, size_t body_length) {
    HttpRequest request;

    if (http_parse_request(listener->request, listener->request_length, &request) < 0) {
        reply(listener, 400, "Bad Request", "text/plain", "bad request\n");

        return;
    }

    if (strcmp(request.method, "PUT") == 0 || strcmp(request.method, "SOURCE") == 0) {
        accept_source(listener, &request, headers, body, body_length);

        return;
    }

    int head_only = strcmp(request.method, "HEAD") == 0;

    if (!head_only && strcmp(request.method, "GET") != 0) {
        reply(listener, 405, "Method Not Allowed", "text/plain", "only GET, HEAD, PUT and SOURCE\n");

        return;
    }

    if (strcmp(request.path, "/admin/metadata") == 0) {
        admin_metadata(listener, &request, headers);

        return;
    }

    if (strcmp(request.path, "/healthz") == 0) {
        reply(listener, 200, "OK", "text/plain", "ok\n");

        return;
    }

    if (strcmp(request.path, "/certificate.sha256") == 0) {
        const char *fingerprint = quic_fingerprint();

        if (fingerprint) {
            reply(listener, 200, "OK", "text/plain", fingerprint);
        }
        else {
            reply(listener, 404, "Not Found", "text/plain", "QUIC is not enabled\n");
        }

        return;
    }

    const char *path = relay_path(request.path);
    Mount *mount = mount_named(path);

    if (strcmp(path, "/moq") == 0) {
        accept_websocket(listener, &request, headers);
    }
    else if (mount) {
        start_stream(listener, &request, head_only, mount);
    }
    else if (strcmp(path, "/events") == 0 || strcmp(path, "/opus/events") == 0) {
        if (head_only) {
            reply(listener, 200, "OK", "text/event-stream", "");
        }
        else {
            start_events(listener, path[1] == 'o' ? &opus : &mp3);
        }
    }
    else if (strcmp(path, "/now") == 0) {
        reply_now(listener, &mp3);
    }
    else if (strcmp(path, "/opus/now") == 0) {
        reply_now(listener, &opus);
    }
    else if (strcmp(path, "/stats") == 0) {
        reply_stats(listener);
    }
    else {
        reply(listener, 404, "Not Found", "text/plain", "not found\n");
    }
}

static void listener_readable(Listener *listener) {
    char discard[1024];

    if (listener->mode == LISTENER_SOURCE) {
        source_readable(listener);

        return;
    }

    if (listener->mode == LISTENER_MOQ) {
        moq_readable(listener);

        return;
    }

    if (listener->mode == LISTENER_MOQL) {
        lite_readable(listener);

        return;
    }

    if (listener->mode != LISTENER_REQUEST) {
        ssize_t got = recv(listener->fd, discard, sizeof(discard), 0);

        if (got == 0 || (got < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
            listener_close(listener);
        }

        return;
    }

    ssize_t got = recv(
        listener->fd, listener->request + listener->request_length, REQUEST_MAX - 1 - listener->request_length, 0);

    if (got == 0 || (got < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
        listener_close(listener);

        return;
    }

    if (got < 0) {
        return;
    }

    listener->request_length += (size_t)got;
    listener->request[listener->request_length] = '\0';

    char *end = memmem(listener->request, listener->request_length, "\r\n\r\n", 4);

    if (end) {
        size_t header_bytes = (size_t)(end - listener->request) + 4;

        end[2] = '\0';

        const char *line_end = strstr(listener->request, "\r\n");

        route(listener, line_end ? line_end + 2 : "", (const unsigned char *)listener->request + header_bytes,
            listener->request_length - header_bytes);
    }
    else if (listener->request_length >= REQUEST_MAX - 1) {
        reply(listener, 431, "Request Header Fields Too Large", "text/plain", "request too large\n");
    }
}

static void accept_listeners(void) {
    for (;;) {
        int fd = accept4(listen_fd, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);

        if (fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            return;
        }

        int slot = -1;

        for (int index = 0; index < config.max_listeners; index++) {
            if (!listeners[index]) {
                slot = index;

                break;
            }
        }

        if (slot < 0) {
            static const char busy[] =
                "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nRetry-After: 5\r\nConnection: close\r\n\r\n";

            send(fd, busy, sizeof(busy) - 1, MSG_NOSIGNAL);
            close(fd);

            stats.rejected++;

            continue;
        }

        Listener *listener = calloc(1, sizeof(Listener));

        if (!listener) {
            close(fd);

            continue;
        }

        int enabled = 1;

        setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &enabled, sizeof(enabled));

        if (config.send_buffer > 0) {
            setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &config.send_buffer, sizeof(config.send_buffer));
        }

        listener->kind = KIND_LISTENER;
        listener->fd = fd;
        listener->slot = slot;
        listener->mode = LISTENER_REQUEST;
        listener->accepted_ms = now_ms();

        listeners[slot] = listener;

        stats.accepted++;

        watch(fd, EPOLLIN | EPOLLRDHUP, listener, 0);
    }
}

static void pump_listeners(void) {
    const StreamTitle *audible_mp3 = audible_title_of(&mp3);
    const StreamTitle *audible_opus = audible_title_of(&opus);
    int64_t now = now_ms();
    int expire_opus = opus.listeners > 0 && !opus_available() && now - opus.lost_ms > (int64_t)config.source_grace * 1000;

    for (int index = 0; index < config.max_listeners; index++) {
        Listener *listener = listeners[index];

        if (!listener) {
            continue;
        }

        if (listener->mode == LISTENER_STREAM) {
            Stream *ring = &listener->mount->ring;

            if (listener->expired || (expire_opus && listener->mount == &opus)) {
                stats.dropped_no_source++;

                listener_close(listener);
            }
            else if (!listener->writable_armed) {
                listener_flush(listener);
            }
            else if (!stream_contains(ring, listener->position) || ring->head - listener->position > config.max_lag_bytes) {
                stats.dropped_lag++;

                listener_close(listener);
            }
        }
        else if (listener->mode == LISTENER_EVENTS) {
            const StreamTitle *audible = listener->mount == &opus ? audible_opus : audible_mp3;

            if (audible && audible->id != listener->event_title_id) {
                send_event(listener, audible);
            }
            else if (now - listener->last_write_ms > 20000 && listener->out_length == 0) {
                listener_print(listener, ": ping\n\n");

                listener->last_write_ms = now;

                listener_flush(listener);
            }
        }
        else if (listener->mode == LISTENER_REQUEST &&
                 now - listener->accepted_ms > (int64_t)config.request_timeout * 1000) {
            stats.dropped_slow_request++;

            listener_close(listener);
        }
        else if (listener->mode == LISTENER_DRAIN && now - listener->accepted_ms > 2000) {
            listener_close(listener);
        }
        else if (listener->mode == LISTENER_SOURCE &&
                 now - listener->mount->source.last_data_ms > (int64_t)config.source_timeout * 1000) {
            source_lost(listener->mount, "went silent");
            listener_close(listener);
        }
        else if (listener->mode == LISTENER_MOQ) {
            if (now - listener->last_seen_ms > (int64_t)config.moq_idle * 1000) {
                listener_close(listener);
            }
            else if (now - listener->last_ping_ms > (int64_t)config.moq_ping * 1000 && listener->out_length == 0) {
                unsigned char header[2];

                listener_add(listener, header, ws_frame_header(header, WS_PING, 0));

                listener->last_ping_ms = now;

                listener_flush(listener);
            }
            else if (!listener->writable_armed) {
                listener_flush(listener);
            }
        }
        else if (listener->mode == LISTENER_MOQL) {
            if (now - listener->last_seen_ms > (int64_t)config.moq_idle * 1000) {
                listener_close(listener);
            }
            else if (!listener->writable_armed) {
                listener_flush(listener);
            }
        }
    }
}

static void on_frame(void *context, const unsigned char *frame, size_t length, const Mp3Header *header) {
    (void)context;

    if (mp3.on_air) {
        return;
    }

    stream_append(&mp3.ring, frame, length, (uint64_t)header->samples * 1000000000ULL / (uint64_t)header->sample_rate);
}

static void on_audio(void *context, const unsigned char *data, size_t length) {
    (void)context;

    mp3_framer_feed(&framer, data, length);
}

static void on_event_line(const char *line) {
    RadioEvent event;

    if (strncmp(line, "data:", 5) != 0 || !radio_event_parse(line + 5, &event)) {
        return;
    }

    if (have_radio_title && strcmp(radio_title.title, event.title) == 0 && strcmp(radio_title.artist, event.artist) == 0 &&
        radio_title.is_request == event.is_request) {
        return;
    }

    radio_title = event;
    have_radio_title = 1;

    if (mp3.on_air) {
        return;
    }

    stream_set_title(&mp3.ring, event.artist, event.album, event.title, event.is_request);

    say("now playing: %s - %s", event.artist, event.title);
}

static void on_events(const unsigned char *data, size_t length) {
    for (size_t index = 0; index < length; index++) {
        char byte = (char)data[index];

        if (byte == '\n') {
            if (events.line_length > 0 && events.line[events.line_length - 1] == '\r') {
                events.line_length--;
            }

            events.line[events.line_length] = '\0';

            on_event_line(events.line);

            events.line_length = 0;
        }
        else if (events.line_length < sizeof(events.line) - 1) {
            events.line[events.line_length++] = byte;
        }
    }
}

static void upstream_drop(Upstream *upstream, const char *why) {
    if (upstream->fd >= 0) {
        epoll_ctl(epoll_fd, EPOLL_CTL_DEL, upstream->fd, NULL);
        close(upstream->fd);

        upstream->fd = -1;
    }

    if (upstream->state != UP_IDLE) {
        int delay = upstream->attempts < 3 ? 1000 * (1 << upstream->attempts) : 5000;

        if (upstream->state == UP_BODY || upstream->attempts % 30 == 0) {
            say("%s source %s, trying again in %d ms", upstream->is_events ? "events" : "audio", why, delay);
        }

        upstream->attempts++;
        upstream->next_attempt_ms = now_ms() + delay;
    }

    upstream->state = UP_IDLE;
    upstream->head_length = 0;
    upstream->line_length = 0;

    if (!upstream->is_events) {
        mp3_framer_reset(&framer);
    }
}

static void on_events_chunk(void *context, const unsigned char *data, size_t length) {
    (void)context;

    on_events(data, length);
}

static void upstream_body(Upstream *upstream, const unsigned char *data, size_t length) {
    upstream->bytes += length;
    upstream->last_data_ms = now_ms();
    upstream->attempts = 0;

    if (upstream->chunked) {
        int result = chunk_decoder_feed(
            &upstream->decoder, data, length, upstream->is_events ? on_events_chunk : on_audio, NULL);

        if (result != 0) {
            upstream_drop(upstream, result > 0 ? "ended the stream" : "sent broken chunks");
        }
    }
    else if (upstream->is_events) {
        on_events(data, length);
    }
    else {
        on_audio(NULL, data, length);
    }
}

static void upstream_connect(Upstream *upstream) {
    int fd = socket(radio_address.ss_family, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);

    if (fd < 0) {
        upstream->state = UP_CONNECTING;

        upstream_drop(upstream, "has no socket");

        return;
    }

    int enabled = 1;

    setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &enabled, sizeof(enabled));
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));

    upstream->fd = fd;
    upstream->state = UP_CONNECTING;
    upstream->last_data_ms = now_ms();

    if (connect(fd, (struct sockaddr *)&radio_address, radio_address_length) < 0 && errno != EINPROGRESS) {
        upstream_drop(upstream, "refused the connection");

        return;
    }

    watch(fd, EPOLLOUT, upstream, 0);
}

static void upstream_writable(Upstream *upstream) {
    int error = 0;
    socklen_t length = sizeof(error);

    if (getsockopt(upstream->fd, SOL_SOCKET, SO_ERROR, &error, &length) < 0 || error != 0) {
        upstream_drop(upstream, "refused the connection");

        return;
    }

    char request[1024];
    int size = snprintf(request, sizeof(request),
        "GET %s HTTP/1.1\r\nHost: %s\r\n%sUser-Agent: caligo-relay\r\n\r\n",
        upstream->is_events ? config.events_path : config.source_path,
        config.source_host,
        upstream->is_events ? "Accept: text/event-stream\r\n" : "");

    if (send(upstream->fd, request, (size_t)size, MSG_NOSIGNAL) != size) {
        upstream_drop(upstream, "did not take the request");

        return;
    }

    upstream->state = UP_HEADERS;
    upstream->head_length = 0;
    upstream->connects++;
    upstream->last_data_ms = now_ms();

    watch(upstream->fd, EPOLLIN | EPOLLRDHUP, upstream, 1);
}

static void upstream_readable(Upstream *upstream) {
    unsigned char buffer[16384];

    for (int round = 0; round < 8; round++) {
        ssize_t got = recv(upstream->fd, buffer, sizeof(buffer), 0);

        if (got < 0) {
            if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
                upstream_drop(upstream, "broke the connection");
            }

            return;
        }

        if (got == 0) {
            upstream_drop(upstream, "closed the connection");

            return;
        }

        if (upstream->state == UP_BODY) {
            upstream_body(upstream, buffer, (size_t)got);

            if (upstream->state != UP_BODY) {
                return;
            }

            continue;
        }

        size_t room = sizeof(upstream->head) - 1 - upstream->head_length;
        size_t take = (size_t)got < room ? (size_t)got : room;

        memcpy(upstream->head + upstream->head_length, buffer, take);

        upstream->head_length += take;
        upstream->head[upstream->head_length] = '\0';

        char *end = strstr(upstream->head, "\r\n\r\n");

        if (!end) {
            if (upstream->head_length >= sizeof(upstream->head) - 1) {
                upstream_drop(upstream, "sent headers that are too long");

                return;
            }

            continue;
        }

        if (strncmp(upstream->head, "HTTP/1.", 7) != 0 || strncmp(upstream->head + 9, "200", 3) != 0) {
            upstream_drop(upstream, "did not answer 200");

            return;
        }

        size_t header_bytes = (size_t)(end + 4 - upstream->head);
        size_t before = upstream->head_length - take;
        size_t body_offset = header_bytes - before;
        char *first_line_end = strstr(upstream->head, "\r\n");
        char encoding[64];

        *end = '\0';

        upstream->chunked =
            first_line_end != end &&
            http_header_value(first_line_end + 2, "Transfer-Encoding", encoding, sizeof(encoding)) &&
            strcasestr(encoding, "chunked") != NULL;

        chunk_decoder_init(&upstream->decoder);

        upstream->state = UP_BODY;

        say("%s source connected%s", upstream->is_events ? "events" : "audio", upstream->chunked ? " (chunked)" : "");

        if ((size_t)got > body_offset) {
            upstream_body(upstream, buffer + body_offset, (size_t)got - body_offset);

            if (upstream->state != UP_BODY) {
                return;
            }
        }
    }
}

static void *resolver_main(void *argument) {
    (void)argument;

    pthread_mutex_lock(&resolver.mutex);

    while (!resolver.stop) {
        while (!resolver.requested && !resolver.stop) {
            pthread_cond_wait(&resolver.wake, &resolver.mutex);
        }

        if (resolver.stop) {
            break;
        }

        resolver.requested = 0;

        char host[256];
        char port[16];

        snprintf(host, sizeof(host), "%s", resolver.host);
        snprintf(port, sizeof(port), "%d", resolver.port);

        pthread_mutex_unlock(&resolver.mutex);

        struct addrinfo hints;
        struct addrinfo *found = NULL;

        memset(&hints, 0, sizeof(hints));

        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;

        int result = getaddrinfo(host, port, &hints, &found);

        pthread_mutex_lock(&resolver.mutex);

        if (result == 0 && found && found->ai_addrlen <= sizeof(resolver.address)) {
            memcpy(&resolver.address, found->ai_addr, found->ai_addrlen);

            resolver.address_length = found->ai_addrlen;
            resolver.failed = 0;
        }
        else {
            resolver.failed = 1;
        }

        resolver.finished = 1;

        if (found) {
            freeaddrinfo(found);
        }

        uint64_t one = 1;

        if (write(resolver.event_fd, &one, sizeof(one)) < 0) {
            resolver.failed = 1;
        }
    }

    pthread_mutex_unlock(&resolver.mutex);

    return NULL;
}

static void resolver_request(void) {
    pthread_mutex_lock(&resolver.mutex);

    resolver.requested = 1;

    pthread_cond_signal(&resolver.wake);
    pthread_mutex_unlock(&resolver.mutex);
}

static void resolver_finished(void) {
    uint64_t count;
    int failed;

    if (read(resolver.event_fd, &count, sizeof(count)) < 0) {
        return;
    }

    pthread_mutex_lock(&resolver.mutex);

    failed = resolver.failed;

    if (!failed) {
        memcpy(&radio_address, &resolver.address, resolver.address_length);

        radio_address_length = resolver.address_length;
        radio_address_ms = now_ms();
    }

    resolver.finished = 0;

    pthread_mutex_unlock(&resolver.mutex);

    Upstream *both[2] = { &audio, &events };

    for (int index = 0; index < 2; index++) {
        if (both[index]->state != UP_RESOLVING) {
            continue;
        }

        if (failed) {
            both[index]->state = UP_CONNECTING;

            upstream_drop(both[index], "could not be found by name");
        }
        else {
            upstream_connect(both[index]);
        }
    }
}

static void upstream_tick(Upstream *upstream) {
    int64_t now = now_ms();

    if (upstream->state == UP_IDLE && now >= upstream->next_attempt_ms) {
        if (radio_address_length > 0 && now - radio_address_ms < 30000 && upstream->attempts == 0) {
            upstream_connect(upstream);
        }
        else {
            upstream->state = UP_RESOLVING;

            resolver_request();
        }

        return;
    }

    int64_t limit = upstream->is_events ? 45 * 60 * 1000 : (int64_t)config.source_timeout * 1000;

    if ((upstream->state == UP_BODY || upstream->state == UP_HEADERS || upstream->state == UP_CONNECTING) &&
        now - upstream->last_data_ms > limit) {
        upstream_drop(upstream, "went silent");
    }
}

static int open_listener(void) {
    struct addrinfo hints;
    struct addrinfo *found = NULL;
    char port[16];

    memset(&hints, 0, sizeof(hints));

    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST;

    snprintf(port, sizeof(port), "%d", config.listen_port);

    if (getaddrinfo(config.listen_host, port, &hints, &found) != 0 || !found) {
        say("RELAY_LISTEN_HOST=%s is not an address", config.listen_host);

        return -1;
    }

    int fd = socket(found->ai_family, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    int enabled = 1;

    if (fd >= 0) {
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    }

    if (fd < 0 || bind(fd, found->ai_addr, found->ai_addrlen) < 0 || listen(fd, 512) < 0) {
        say("cannot listen on %s:%d: %s", config.listen_host, config.listen_port, strerror(errno));

        if (fd >= 0) {
            close(fd);
        }

        freeaddrinfo(found);

        return -1;
    }

    freeaddrinfo(found);

    return fd;
}

static void on_signal(int number) {
    (void)number;

    running = 0;
}

int main(void) {
    setvbuf(stderr, NULL, _IOLBF, 0);

    load_config();

    signal(SIGPIPE, SIG_IGN);
    signal(SIGTERM, on_signal);
    signal(SIGINT, on_signal);

    if (stream_init(&mp3.ring, config.ring_bytes) < 0 || stream_init(&opus.ring, config.ring_bytes) < 0) {
        say("cannot allocate the stream buffers");

        return 1;
    }

    opus.ring.title_id_base = 1ULL << 40;
    opus.follows = 1;

    uint32_t serial = 0;

    if (getrandom(&serial, sizeof(serial), 0) != (ssize_t)sizeof(serial)) {
        serial = (uint32_t)now_ms();
    }

    struct timespec wall;

    clock_gettime(CLOCK_REALTIME, &wall);

    uint64_t first_group = (uint64_t)wall.tv_sec * 1000 + (uint64_t)wall.tv_nsec / 1000000;

    if (object_track_init(&audio_track, 2 * 1024 * 1024, 16384, first_group, (uint64_t)config.moq_group_ms * 48) < 0 ||
        object_track_init(&meta_track, 65536, 64, first_group, 0) < 0) {
        say("cannot allocate the object tracks");

        return 1;
    }

    mp3_framer_init(&framer, on_frame, NULL);
    mp3_framer_init(&live_framer, on_live_frame, NULL);
    ogg_reader_init(&ogg_reader, on_ogg_page, NULL);
    opus_splicer_init(&splicer, serial, on_opus_page, on_opus_title, on_opus_reset, NULL);
    opus_splicer_packets(&splicer, on_opus_packet);
    catalog_update();

    lite_source.audio = &audio_track;
    lite_source.meta = &meta_track;
    lite_source.catalog = (const unsigned char *)catalog_json;

    if (getrandom(&lite_hop, sizeof(lite_hop), 0) != (ssize_t)sizeof(lite_hop)) {
        lite_hop = (uint64_t)now_ms();
    }

    lite_hop = (lite_hop & ((1ULL << 53) - 1)) | 1;

    listeners = calloc((size_t)config.max_listeners, sizeof(Listener *));
    epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    listen_fd = open_listener();

    if (!listeners || epoll_fd < 0 || listen_fd < 0) {
        return 1;
    }

    audio.kind = KIND_UPSTREAM;
    audio.fd = -1;
    events.kind = KIND_UPSTREAM;
    events.is_events = 1;
    events.fd = -1;

    pthread_mutex_init(&resolver.mutex, NULL);
    pthread_cond_init(&resolver.wake, NULL);

    snprintf(resolver.host, sizeof(resolver.host), "%s", config.source_host);

    resolver.port = config.source_port;
    resolver.event_fd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);

    if (resolver.event_fd < 0 || pthread_create(&resolver.thread, NULL, resolver_main, NULL) != 0) {
        say("cannot start the name resolver");

        return 1;
    }

    watch(listen_fd, EPOLLIN, &listen_kind, 0);
    watch(resolver.event_fd, EPOLLIN, &resolver_kind, 0);

    QuicConfig quic_config = {
        .host = config.listen_host,
        .port = config.quic_port,
        .cert_file = config.quic_cert,
        .key_file = config.quic_key,
        .max_sessions = config.quic_max_sessions,
        .idle_seconds = config.quic_idle,
        .epoll_fd = epoll_fd,
        .epoll_tag = &quic_kind,
    };

    if (quic_start(&quic_config, &lite_source, lite_hop) < 0) {
        return 1;
    }

    stats.started_ms = now_ms();

    say("listening on %s:%d, radio %s:%d%s, burst %zu bytes, lag limit %zu bytes, sources %s",
        config.listen_host, config.listen_port, config.source_host, config.source_port, config.source_path,
        config.burst_bytes, config.max_lag_bytes,
        config.source_password[0] ? "accepted at /live/ and /live/opus" : "switched off");

    int64_t last_tick = 0;

    while (running) {
        struct epoll_event ready[256];
        int count = epoll_wait(epoll_fd, ready, 256, quic_timeout(now_ms(), 250));

        if (count < 0 && errno != EINTR) {
            say("epoll_wait failed: %s", strerror(errno));

            break;
        }

        uint64_t changes_before = mp3.ring.head + opus.ring.head + mp3.ring.title_count + opus.ring.title_count +
            audio_track.count + meta_track.count + catalog_version;

        for (int index = 0; index < count; index++) {
            int kind = *(int *)ready[index].data.ptr;
            uint32_t happened = ready[index].events;

            if (kind == KIND_LISTEN) {
                accept_listeners();
            }
            else if (kind == KIND_RESOLVER) {
                resolver_finished();
            }
            else if (kind == KIND_QUIC) {
                quic_readable(now_ms());
            }
            else if (kind == KIND_UPSTREAM) {
                Upstream *upstream = ready[index].data.ptr;

                if (upstream->fd < 0) {
                    continue;
                }

                if (upstream->state == UP_CONNECTING) {
                    if (happened & (EPOLLOUT | EPOLLERR | EPOLLHUP)) {
                        upstream_writable(upstream);
                    }
                }
                else if (happened & (EPOLLIN | EPOLLERR | EPOLLHUP | EPOLLRDHUP)) {
                    upstream_readable(upstream);
                }
            }
            else {
                Listener *listener = ready[index].data.ptr;
                int slot = listener->slot;

                if ((happened & (EPOLLERR | EPOLLHUP)) && !((happened & EPOLLIN) && listener->mode == LISTENER_SOURCE)) {
                    listener_close(listener);

                    continue;
                }

                if (happened & (EPOLLIN | EPOLLRDHUP)) {
                    listener_readable(listener);
                }

                if (listeners[slot] == listener && (happened & EPOLLOUT)) {
                    listener_flush(listener);
                }
            }
        }

        meta_update();

        lite_source.catalog_length = catalog_length;
        lite_source.catalog_version = catalog_version;
        lite_source.live = opus_available();

        int64_t now = now_ms();
        uint64_t changes_after = mp3.ring.head + opus.ring.head + mp3.ring.title_count + opus.ring.title_count +
            audio_track.count + meta_track.count + catalog_version;

        if (changes_after != changes_before || now - last_tick >= 250) {
            pump_listeners();
        }

        quic_service(now);

        if (now - last_tick >= 250) {
            last_tick = now;

            upstream_tick(&audio);
            upstream_tick(&events);
            catalog_update();
        }
    }

    say("stopping");

    quic_stop();

    for (int index = 0; index < config.max_listeners; index++) {
        if (listeners[index]) {
            listener_close(listeners[index]);
        }
    }

    pthread_mutex_lock(&resolver.mutex);

    resolver.stop = 1;

    pthread_cond_signal(&resolver.wake);
    pthread_mutex_unlock(&resolver.mutex);
    pthread_join(resolver.thread, NULL);

    stream_free(&mp3.ring);
    stream_free(&opus.ring);
    object_track_free(&audio_track);
    object_track_free(&meta_track);
    free(listeners);

    return 0;
}
