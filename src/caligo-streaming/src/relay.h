#ifndef RELAY_H
#define RELAY_H

#include <netinet/in.h>
#include <pthread.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/socket.h>

#include "http.h"
#include "moqlite.h"
#include "mp3.h"
#include "objects.h"
#include "ogg.h"
#include "quic.h"
#include "stream.h"
#include "websocket.h"

#define KIND_LISTEN 1
#define KIND_RESOLVER 2
#define KIND_UPSTREAM 3
#define KIND_LISTENER 4
#define KIND_QUIC 5

#define REQUEST_MAX 4096
#define OUT_MAX 4096
#define MAX_CHUNK 16384
#define SESSION_SLOTS 4096
#define SESSION_PROBES 8

typedef struct {
    char listen_host[64];
    int listen_port;
    char source_host[256];
    int source_port;
    char source_path[256];
    char events_path[256];
    char station[128];
    char source_user[64];
    char source_password[256];
    size_t burst_bytes;
    size_t max_lag_bytes;
    size_t ring_bytes;
    int max_listeners;
    int send_buffer;
    int icy_metaint;
    int source_timeout;
    int source_grace;
    int source_max_ahead;
    int request_timeout;
    int session_ttl;
    int moq_group_ms;
    int moq_ping;
    int moq_idle;
    int moq_send_buffer;
    int quic_port;
    char quic_cert[256];
    char quic_key[256];
    int quic_max_sessions;
    int quic_idle;
} Config;

typedef enum { UP_IDLE, UP_RESOLVING, UP_CONNECTING, UP_HEADERS, UP_BODY } UpstreamState;

typedef struct {
    int kind;
    int is_events;
    int fd;
    UpstreamState state;
    char head[4096];
    size_t head_length;
    int chunked;
    ChunkDecoder decoder;
    char line[4096];
    size_t line_length;
    int64_t next_attempt_ms;
    int64_t last_data_ms;
    int attempts;
    uint64_t connects;
    uint64_t bytes;
} Upstream;

typedef enum {
    LISTENER_REQUEST,
    LISTENER_STREAM,
    LISTENER_EVENTS,
    LISTENER_REPLY,
    LISTENER_DRAIN,
    LISTENER_SOURCE,
    LISTENER_MOQ,
    LISTENER_MOQL
} ListenerMode;

typedef struct Mount Mount;

typedef struct {
    int kind;
    int fd;
    int slot;
    ListenerMode mode;
    Mount *mount;
    char request[REQUEST_MAX];
    size_t request_length;
    unsigned char out[OUT_MAX];
    size_t out_length;
    size_t out_offset;
    uint64_t position;
    uint64_t ring_left;
    int chunked;
    int trailer_due;
    int icy;
    size_t icy_left;
    uint64_t icy_title_id;
    uint64_t event_title_id;
    int writable_armed;
    int expired;
    int64_t accepted_ms;
    int64_t last_write_ms;
    uint64_t sent;
    int body_chunked;
    ChunkDecoder body_decoder;
    int64_t body_left;
    uint64_t audio_next;
    uint64_t meta_next;
    uint64_t catalog_sent;
    int64_t last_seen_ms;
    int64_t last_ping_ms;
    MoqlSession *moql;
    unsigned char *ws_in;
    size_t ws_in_length;
} Listener;

typedef struct {
    Listener *connection;
    char method[8];
    char agent[128];
    char name[128];
    int64_t started_ms;
    int64_t last_data_ms;
    int64_t window_ms;
    uint64_t window_ns;
    uint64_t bytes;
    uint64_t media_ns;
    const char *kill;
} Source;

struct Mount {
    const char *path;
    const char *type;
    Stream ring;
    Source source;
    int on_air;
    int follows;
    int listeners;
    int peak_listeners;
    int64_t lost_ms;
    uint64_t connects;
    uint64_t drops;
    char last_drop[64];
    OpusTitle live_title;
    int has_live_title;
};

typedef struct {
    uint64_t key;
    uint64_t base;
    int64_t used_ms;
} Session;

typedef struct {
    pthread_t thread;
    pthread_mutex_t mutex;
    pthread_cond_t wake;
    int event_fd;
    int requested;
    int finished;
    int failed;
    int stop;
    char host[256];
    int port;
    struct sockaddr_storage address;
    socklen_t address_length;
} Resolver;

typedef struct {
    int64_t started_ms;
    uint64_t accepted;
    uint64_t rejected;
    uint64_t dropped_lag;
    uint64_t dropped_slow_request;
    uint64_t dropped_no_source;
    uint64_t resumed_exact;
    uint64_t resumed_fresh;
    uint64_t bytes_out;
    uint64_t sources_unauthorized;
    uint64_t sources_refused;
    uint64_t metadata_updates;
    uint64_t moq_accepted;
    uint64_t moq_refused;
    uint64_t moq_skips;
    uint64_t moq_objects_out;
    uint64_t moq_too_big;
    int event_listeners;
    int moq_connections;
    int moq_peak;
    uint64_t lite_accepted;
    uint64_t lite_failures;
    int lite_connections;
    int lite_peak;
} Stats;

#endif
