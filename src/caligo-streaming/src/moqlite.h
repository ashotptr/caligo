#ifndef RELAY_MOQLITE_H
#define RELAY_MOQLITE_H

#include <stddef.h>
#include <stdint.h>

#include "objects.h"
#include "qmux.h"

#define MOQL_STREAMS 64
#define MOQL_SUBSCRIPTIONS 16
#define MOQL_RECORD QMUX_DEFAULT_RECORD
#define MOQL_OUT_MAX 65536
#define MOQL_IN_MAX 16384
#define MOQL_PENDING_MAX 8192
#define MOQL_PING_MS 10000
#define MOQL_AUDIO_MAX_AGE_MS 2000
#define MOQL_TEXT_MAX_AGE_MS 5000

#define MOQL_TRACK_AUDIO 0
#define MOQL_TRACK_META 1
#define MOQL_TRACK_CATALOG 2

#define MOQL_ERROR_CANCELLED 0x1
#define MOQL_ERROR_NOT_FOUND 0x33
#define MOQL_ERROR_OLD 0x34

#define MOQL_NO_STREAM UINT64_MAX

typedef struct {
    const char *broadcast;
    const ObjectTrack *audio;
    const ObjectTrack *meta;
    const unsigned char *catalog;
    size_t catalog_length;
    uint64_t catalog_version;
    int live;
} MoqlSource;

typedef struct {
    void *context;
    uint64_t (*open_uni)(void *context);
    void (*wake)(void *context, uint64_t stream);
    void (*reset)(void *context, uint64_t stream, uint64_t code);
    void (*stop)(void *context, uint64_t stream, uint64_t code);
} MoqlTransport;

typedef unsigned char *(*MoqlBufferFn)(void *context, size_t length, int fin, int more);

typedef struct {
    int used;
    uint64_t id;
    int kind;
    uint64_t recv_offset;
    uint64_t recv_window;
    int recv_done;
    unsigned char *in;
    size_t in_length;
    uint64_t send_offset;
    uint64_t send_limit;
    unsigned char *pending;
    size_t pending_length;
    int fin_queued;
    int send_done;
    int reset_pending;
    uint64_t reset_code;
    int stop_pending;
    uint64_t stop_code;
    int subscription;
    int group_stream;
    int woken;
} MoqlStream;

typedef struct {
    int used;
    uint64_t id;
    int track;
    int control;
    int group_stream;
    uint64_t group;
    uint64_t next;
    uint64_t prev_timestamp;
    uint64_t min_group;
    uint64_t min_frame;
    uint64_t end_group;
    uint64_t catalog_sent;
    int started;
} MoqlSubscription;

typedef struct {
    MoqlSource *source;
    const MoqlTransport *transport;
    int setup_opened;
    int version;
    QmuxParams peer;
    int peer_known;
    uint64_t hop;
    uint64_t send_total;
    uint64_t recv_total;
    uint64_t recv_max;
    uint64_t opened_uni;
    uint64_t peer_bidi_closed;
    uint64_t peer_uni_closed;
    uint64_t max_bidi_granted;
    uint64_t max_uni_granted;
    uint64_t ping_sent;
    MoqlStream streams[MOQL_STREAMS];
    uint64_t closed_ids[MOQL_STREAMS];
    unsigned closed_next;
    MoqlSubscription subscriptions[MOQL_SUBSCRIPTIONS];
    unsigned char control[1024];
    size_t control_length;
    unsigned char *out;
    size_t out_length;
    size_t out_offset;
    int64_t last_record_ms;
    int closing;
    const char *error;
    uint64_t records_in;
    uint64_t records_out;
    uint64_t groups_out;
    uint64_t frames_out;
    uint64_t skips;
} MoqlSession;

MoqlSession *moql_create(MoqlSource *source, int version, uint64_t hop, int64_t now);

MoqlSession *moql_create_native(MoqlSource *source, int version, uint64_t hop, const MoqlTransport *transport, int64_t now);

void moql_free(MoqlSession *session);

int moql_stream_data(MoqlSession *session, uint64_t stream, const unsigned char *data, size_t length, int fin);

void moql_stream_reset(MoqlSession *session, uint64_t stream);

void moql_stream_stop(MoqlSession *session, uint64_t stream, uint64_t code);

int moql_stream_send(MoqlSession *session, uint64_t stream, size_t space, MoqlBufferFn buffer, void *context);

int moql_receive(MoqlSession *session, const unsigned char *record, size_t length, int64_t now);

void moql_pump(MoqlSession *session, int64_t now);

void moql_raw(MoqlSession *session, const unsigned char *bytes, size_t length);

void moql_close(MoqlSession *session, uint64_t code, const char *reason);

size_t moql_pending(const MoqlSession *session);

void moql_consumed(MoqlSession *session, size_t length);

#endif
