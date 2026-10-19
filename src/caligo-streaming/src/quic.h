#ifndef RELAY_QUIC_H
#define RELAY_QUIC_H

#include <stddef.h>
#include <stdint.h>

#include "moqlite.h"

#define QUIC_PROTOCOL "moq-lite-06"
#define QUIC_PATH "/live/moq"

typedef struct {
    const char *host;
    int port;
    const char *cert_file;
    const char *key_file;
    int max_sessions;
    int idle_seconds;
    int epoll_fd;
    void *epoll_tag;
} QuicConfig;

typedef struct {
    int enabled;
    int port;
    int sessions;
    int peak;
    uint64_t accepted;
    uint64_t bare_accepted;
    uint64_t refused;
    uint64_t failures;
    uint64_t closed;
    uint64_t packets_in;
    uint64_t packets_out;
    uint64_t groups_out;
    uint64_t frames_out;
    uint64_t skips;
    int64_t certificate_valid_ms;
} QuicStats;

int quic_start(const QuicConfig *config, MoqlSource *source, uint64_t hop);

void quic_readable(int64_t now);

void quic_service(int64_t now);

int quic_timeout(int64_t now, int limit);

const char *quic_fingerprint(void);

void quic_stats(QuicStats *stats);

void quic_stop(void);

#endif
