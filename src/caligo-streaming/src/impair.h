#ifndef NETEM_IMPAIR_H
#define NETEM_IMPAIR_H

#include <stddef.h>
#include <stdint.h>

#define IMPAIR_TEXT_MAX 256
#define IMPAIR_DEFAULT_QUEUE_MS 200

typedef struct {
    uint32_t delay_ms;
    uint32_t jitter_ms;
    double loss;
    double loss_burst;
    uint64_t rate_bps;
    uint32_t queue_ms;
    int per_flow;
} ImpairProfile;

typedef struct {
    ImpairProfile profile;
    uint64_t random;
    int64_t link_free_us;
    int64_t last_release_us;
    int last_lost;
    uint64_t packets;
    uint64_t bytes;
    uint64_t lost;
    uint64_t overflowed;
} Impair;

void impair_init(Impair *impair, const ImpairProfile *profile, uint64_t seed);

int64_t impair_packet(Impair *impair, size_t size, int64_t now_us);

int impair_parse(const char *text, ImpairProfile *profile);

size_t impair_describe(const ImpairProfile *profile, char *out, size_t capacity);

#endif
