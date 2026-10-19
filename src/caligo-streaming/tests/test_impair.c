#include "check.h"
#include "impair.h"

static void profiles_parse_and_describe(void) {
    ImpairProfile profile;
    char text[IMPAIR_TEXT_MAX];

    CHECK_EQUAL(0, impair_parse("delay_ms=80 jitter_ms=5\nloss=0.02 loss_burst=0.3 rate_kbit=2000", &profile));
    CHECK_EQUAL(80, profile.delay_ms);
    CHECK_EQUAL(5, profile.jitter_ms);
    CHECK(profile.loss > 0.0199 && profile.loss < 0.0201);
    CHECK(profile.loss_burst > 0.2999 && profile.loss_burst < 0.3001);
    CHECK_EQUAL(2000000, profile.rate_bps);
    CHECK_EQUAL(IMPAIR_DEFAULT_QUEUE_MS, profile.queue_ms);

    impair_describe(&profile, text, sizeof(text));

    CHECK_TEXT("delay_ms=80 jitter_ms=5 loss=0.0200 loss_burst=0.3000 rate_kbit=2000 queue_ms=200 scope=link", text);

    ImpairProfile again;

    CHECK_EQUAL(0, impair_parse(text, &again));
    CHECK(again.delay_ms == profile.delay_ms && again.rate_bps == profile.rate_bps && again.queue_ms == profile.queue_ms);
    CHECK_EQUAL(0, again.per_flow);
    CHECK_EQUAL(0, impair_parse("rate_kbit=5000 scope=flow", &again));
    CHECK_EQUAL(1, again.per_flow);
    CHECK_EQUAL(-1, impair_parse("scope=host", &again));

    CHECK_EQUAL(0, impair_parse("off", &profile));
    CHECK(profile.delay_ms == 0 && profile.loss == 0 && profile.rate_bps == 0 && profile.queue_ms == 0);
    CHECK_EQUAL(0, impair_parse("", &profile));
    CHECK_EQUAL(-1, impair_parse("delay=5", &profile));
    CHECK_EQUAL(-1, impair_parse("loss=1.5", &profile));
    CHECK_EQUAL(-1, impair_parse("delay_ms=-3", &profile));
    CHECK_EQUAL(-1, impair_parse("delay_ms=5ms", &profile));
    CHECK_EQUAL(-1, impair_parse("queue_ms=0", &profile));
    CHECK_EQUAL(-1, impair_parse("rate_kbit", &profile));
}

static void delay_and_jitter_keep_order(void) {
    ImpairProfile profile;
    Impair impair;
    int64_t previous = 0;
    int64_t smallest = INT64_MAX;
    int64_t largest = 0;
    int ordered = 1;

    impair_parse("delay_ms=50", &profile);
    impair_init(&impair, &profile, 7);

    CHECK_EQUAL(1050000, impair_packet(&impair, 1200, 1000000));
    CHECK_EQUAL(1060000, impair_packet(&impair, 1200, 1010000));

    impair_parse("delay_ms=50 jitter_ms=10", &profile);
    impair_init(&impair, &profile, 11);

    for (int index = 0; index < 5000; index++) {
        int64_t now = 2000000 + (int64_t)index * 20000;
        int64_t release = impair_packet(&impair, 1200, now);
        int64_t wait = release - now;

        ordered &= release >= previous;
        previous = release;

        if (wait < smallest) {
            smallest = wait;
        }

        if (wait > largest) {
            largest = wait;
        }
    }

    CHECK(ordered);
    CHECK(smallest >= 40000 && smallest < 41000);
    CHECK(largest <= 60000 && largest > 59000);
}

static void loss_and_bursts(void) {
    ImpairProfile profile;
    Impair impair;
    int lost = 0;
    int bursts = 0;
    int in_burst = 0;

    impair_parse("loss=0.1", &profile);
    impair_init(&impair, &profile, 99);

    for (int index = 0; index < 100000; index++) {
        lost += impair_packet(&impair, 100, index) < 0;
    }

    CHECK(lost > 9300 && lost < 10700);
    CHECK_EQUAL((uint64_t)lost, impair.lost);

    impair_parse("loss=0.05 loss_burst=0.5", &profile);
    impair_init(&impair, &profile, 5);

    lost = 0;

    for (int index = 0; index < 200000; index++) {
        int dropped = impair_packet(&impair, 100, index) < 0;

        lost += dropped;
        bursts += dropped && !in_burst;
        in_burst = dropped;
    }

    double mean_burst = (double)lost / bursts;

    CHECK(mean_burst > 1.85 && mean_burst < 2.15);

    impair_parse("loss=0", &profile);
    impair_init(&impair, &profile, 3);

    lost = 0;

    for (int index = 0; index < 1000; index++) {
        lost += impair_packet(&impair, 100, index) < 0;
    }

    CHECK_EQUAL(0, lost);
}

static void rate_serializes_and_tail_drops(void) {
    ImpairProfile profile;
    Impair impair;
    int64_t releases[12];

    impair_parse("rate_kbit=1000 queue_ms=100", &profile);
    impair_init(&impair, &profile, 1);

    for (int index = 0; index < 12; index++) {
        releases[index] = impair_packet(&impair, 1250, 0);
    }

    CHECK_EQUAL(10000, releases[0]);
    CHECK_EQUAL(20000, releases[1]);
    CHECK_EQUAL(100000, releases[9]);
    CHECK_EQUAL(110000, releases[10]);
    CHECK_EQUAL(-1, releases[11]);
    CHECK_EQUAL(1, impair.overflowed);
    CHECK_EQUAL(-1, impair_packet(&impair, 1250, 5000));
    CHECK_EQUAL(120000, impair_packet(&impair, 1250, 15000));

    impair_parse("rate_kbit=1000 queue_ms=50 delay_ms=20", &profile);
    impair_init(&impair, &profile, 1);

    int delivered = 0;
    int64_t last = 0;

    for (int index = 0; index < 2000; index++) {
        int64_t release = impair_packet(&impair, 1250, (int64_t)index * 5000);

        if (release >= 0) {
            delivered++;
            last = release;
        }
    }

    CHECK(delivered > 990 && delivered < 1010);
    CHECK(last > 10000000 && last < 10100000);
}

int main(void) {
    profiles_parse_and_describe();
    delay_and_jitter_keep_order();
    loss_and_bursts();
    rate_serializes_and_tail_drops();

    CHECK_REPORT("impair");
}
