#include "impair.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t next_random(Impair *impair) {
    uint64_t value = impair->random;

    value ^= value << 13;
    value ^= value >> 7;
    value ^= value << 17;

    impair->random = value;

    return value;
}

static double uniform(Impair *impair) {
    return (double)(next_random(impair) >> 11) / 9007199254740992.0;
}

void impair_init(Impair *impair, const ImpairProfile *profile, uint64_t seed) {
    memset(impair, 0, sizeof(*impair));

    impair->profile = *profile;
    impair->random = seed ? seed : 0x9e3779b97f4a7c15ULL;
}

int64_t impair_packet(Impair *impair, size_t size, int64_t now_us) {
    const ImpairProfile *profile = &impair->profile;
    double chance = impair->last_lost && profile->loss_burst > profile->loss ? profile->loss_burst : profile->loss;

    impair->packets++;
    impair->bytes += size;

    if (chance > 0 && uniform(impair) < chance) {
        impair->last_lost = 1;
        impair->lost++;

        return -1;
    }

    impair->last_lost = 0;

    int64_t depart = now_us;

    if (profile->rate_bps > 0) {
        int64_t start = impair->link_free_us > now_us ? impair->link_free_us : now_us;
        int64_t limit = (int64_t)(profile->queue_ms ? profile->queue_ms : IMPAIR_DEFAULT_QUEUE_MS) * 1000;

        if (start - now_us > limit) {
            impair->overflowed++;

            return -1;
        }

        depart = start + (int64_t)((double)size * 8000000.0 / (double)profile->rate_bps);
        impair->link_free_us = depart;
    }

    int64_t release = depart + (int64_t)profile->delay_ms * 1000;

    if (profile->jitter_ms > 0) {
        int64_t spread = (int64_t)profile->jitter_ms * 1000;

        release += (int64_t)(uniform(impair) * (double)(2 * spread + 1)) - spread;
    }

    if (release < impair->last_release_us) {
        release = impair->last_release_us;
    }

    if (release < now_us) {
        release = now_us;
    }

    impair->last_release_us = release;

    return release;
}

static int read_number(const char *value, double minimum, double maximum, double *out) {
    char *end = NULL;
    double number = strtod(value, &end);

    if (end == value || *end != '\0' || number < minimum || number > maximum) {
        return -1;
    }

    *out = number;

    return 0;
}

int impair_parse(const char *text, ImpairProfile *profile) {
    char copy[IMPAIR_TEXT_MAX];
    char *cursor = copy;

    memset(profile, 0, sizeof(*profile));

    if (strlen(text) >= sizeof(copy)) {
        return -1;
    }

    snprintf(copy, sizeof(copy), "%s", text);

    for (;;) {
        while (*cursor && isspace((unsigned char)*cursor)) {
            cursor++;
        }

        if (!*cursor) {
            break;
        }

        char *token = cursor;

        while (*cursor && !isspace((unsigned char)*cursor)) {
            cursor++;
        }

        if (*cursor) {
            *cursor++ = '\0';
        }

        if (strcmp(token, "off") == 0) {
            continue;
        }

        char *equals = strchr(token, '=');
        double number = 0;

        if (!equals) {
            return -1;
        }

        *equals = '\0';

        const char *name = token;
        const char *value = equals + 1;

        if (strcmp(name, "delay_ms") == 0 && read_number(value, 0, 10000, &number) == 0) {
            profile->delay_ms = (uint32_t)number;
        }
        else if (strcmp(name, "jitter_ms") == 0 && read_number(value, 0, 10000, &number) == 0) {
            profile->jitter_ms = (uint32_t)number;
        }
        else if (strcmp(name, "loss") == 0 && read_number(value, 0, 1, &number) == 0) {
            profile->loss = number;
        }
        else if (strcmp(name, "loss_burst") == 0 && read_number(value, 0, 1, &number) == 0) {
            profile->loss_burst = number;
        }
        else if (strcmp(name, "rate_kbit") == 0 && read_number(value, 0, 10000000, &number) == 0) {
            profile->rate_bps = (uint64_t)(number * 1000);
        }
        else if (strcmp(name, "queue_ms") == 0 && read_number(value, 1, 10000, &number) == 0) {
            profile->queue_ms = (uint32_t)number;
        }
        else if (strcmp(name, "scope") == 0 && (strcmp(value, "flow") == 0 || strcmp(value, "link") == 0)) {
            profile->per_flow = strcmp(value, "flow") == 0;
        }
        else {
            return -1;
        }
    }

    if (profile->rate_bps > 0 && profile->queue_ms == 0) {
        profile->queue_ms = IMPAIR_DEFAULT_QUEUE_MS;
    }

    return 0;
}

size_t impair_describe(const ImpairProfile *profile, char *out, size_t capacity) {
    int written = snprintf(out, capacity, "delay_ms=%u jitter_ms=%u loss=%.4f loss_burst=%.4f rate_kbit=%llu queue_ms=%u scope=%s",
        profile->delay_ms, profile->jitter_ms, profile->loss, profile->loss_burst,
        (unsigned long long)(profile->rate_bps / 1000), profile->queue_ms, profile->per_flow ? "flow" : "link");

    return written < 0 ? 0 : (size_t)written < capacity ? (size_t)written : capacity - 1;
}
