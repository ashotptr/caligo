#include "check.h"
#include "ladder.h"

static Rung rungs[LADDER_MAX];
static int rung_count;

static void the_default_ladder_is_read_and_sorted(void) {
    Rung parsed[LADDER_MAX];

    rung_count = ladder_parse("1080:5000,720:2800,480:1400,360:800,240:400", rungs, LADDER_MAX);

    CHECK_EQUAL(5, rung_count);
    CHECK_EQUAL(1080, rungs[0].height);
    CHECK_EQUAL(400, rungs[4].kbps);

    CHECK_EQUAL(3, ladder_parse("360:800,1080:5000,720:2800", parsed, LADDER_MAX));
    CHECK_EQUAL(1080, parsed[0].height);
    CHECK_EQUAL(720, parsed[1].height);
    CHECK_EQUAL(360, parsed[2].height);

    CHECK_EQUAL(-1, ladder_parse("", parsed, LADDER_MAX));
    CHECK_EQUAL(-1, ladder_parse("1080", parsed, LADDER_MAX));
    CHECK_EQUAL(1, ladder_parse("1080:5000,", parsed, LADDER_MAX));
    CHECK_EQUAL(-1, ladder_parse("1080:abc", parsed, LADDER_MAX));
    CHECK_EQUAL(-1, ladder_parse("1080:50", parsed, LADDER_MAX));
    CHECK_EQUAL(-1, ladder_parse("4:5000", parsed, LADDER_MAX));
    CHECK_EQUAL(-1, ladder_parse("720:2800,720:2800,720:2800", parsed, 2));
}

static void a_720p_source_starts_at_its_own_size(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(1280, 720, rungs, rung_count, outputs, LADDER_MAX);

    CHECK_EQUAL(4, count);
    CHECK_EQUAL(1280, outputs[0].width);
    CHECK_EQUAL(720, outputs[0].height);
    CHECK_EQUAL(2800, outputs[0].kbps);
    CHECK_EQUAL(854, outputs[1].width);
    CHECK_EQUAL(480, outputs[1].height);
    CHECK_EQUAL(640, outputs[2].width);
    CHECK_EQUAL(360, outputs[2].height);
    CHECK_EQUAL(426, outputs[3].width);
    CHECK_EQUAL(240, outputs[3].height);
    CHECK_EQUAL(1, ladder_progressive(outputs, count, 480));
}

static void a_wide_film_keeps_its_full_width(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(1920, 800, rungs, rung_count, outputs, LADDER_MAX);

    CHECK_EQUAL(5, count);
    CHECK_EQUAL(1920, outputs[0].width);
    CHECK_EQUAL(800, outputs[0].height);
    CHECK(outputs[0].kbps < 5000 && outputs[0].kbps > 3500);
    CHECK_EQUAL(1280, outputs[1].width);
    CHECK_EQUAL(534, outputs[1].height);
    CHECK_EQUAL(2800, outputs[1].kbps);

    for (int i = 0; i < count; i++) {
        CHECK_EQUAL(0, outputs[i].width % 2);
        CHECK_EQUAL(0, outputs[i].height % 2);
    }
}

static void a_4k_source_is_scaled_into_every_rung(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(3840, 2160, rungs, rung_count, outputs, LADDER_MAX);

    CHECK_EQUAL(5, count);
    CHECK_EQUAL(1920, outputs[0].width);
    CHECK_EQUAL(1080, outputs[0].height);
    CHECK_EQUAL(5000, outputs[0].kbps);
    CHECK_EQUAL(426, outputs[4].width);
}

static void a_tiny_source_gets_one_rendition(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(320, 180, rungs, rung_count, outputs, LADDER_MAX);

    CHECK_EQUAL(1, count);
    CHECK_EQUAL(320, outputs[0].width);
    CHECK_EQUAL(180, outputs[0].height);
    CHECK(outputs[0].kbps >= LADDER_MIN_KBPS && outputs[0].kbps < 400);
    CHECK_EQUAL(0, ladder_progressive(outputs, count, 480));
}

static void a_portrait_video_stays_portrait(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(1080, 1920, rungs, rung_count, outputs, LADDER_MAX);

    CHECK_EQUAL(5, count);
    CHECK_EQUAL(1080, outputs[0].width);
    CHECK_EQUAL(1920, outputs[0].height);
    CHECK_EQUAL(720, outputs[1].width);
    CHECK_EQUAL(1280, outputs[1].height);
    CHECK_EQUAL(2, ladder_progressive(outputs, count, 480));
}

static void odd_sizes_become_even(void) {
    Output outputs[LADDER_MAX];
    int count = ladder_plan(721, 405, rungs, rung_count, outputs, LADDER_MAX);

    CHECK(count >= 2);
    CHECK_EQUAL(720, outputs[0].width);
    CHECK_EQUAL(404, outputs[0].height);

    for (int i = 0; i < count; i++) {
        CHECK_EQUAL(0, outputs[i].width % 2);
        CHECK_EQUAL(0, outputs[i].height % 2);
    }

    CHECK_EQUAL(0, ladder_plan(1, 1, rungs, rung_count, outputs, LADDER_MAX));
}

int main(void) {
    the_default_ladder_is_read_and_sorted();
    a_720p_source_starts_at_its_own_size();
    a_wide_film_keeps_its_full_width();
    a_4k_source_is_scaled_into_every_rung();
    a_tiny_source_gets_one_rendition();
    a_portrait_video_stays_portrait();
    odd_sizes_become_even();

    CHECK_REPORT("ladder");
}
