#include "quic.h"

#include <string.h>

int quic_start(const QuicConfig *config, MoqlSource *source, uint64_t hop) {
    (void)config;
    (void)source;
    (void)hop;

    return 0;
}

void quic_readable(int64_t now) {
    (void)now;
}

void quic_service(int64_t now) {
    (void)now;
}

int quic_timeout(int64_t now, int limit) {
    (void)now;

    return limit;
}

const char *quic_fingerprint(void) {
    return NULL;
}

void quic_stats(QuicStats *stats) {
    memset(stats, 0, sizeof(*stats));
}

void quic_stop(void) {
}
