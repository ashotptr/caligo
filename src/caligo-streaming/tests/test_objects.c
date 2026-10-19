#include "check.h"
#include "objects.h"

static void varints_follow_rfc_9000(void) {
    const unsigned char one[] = { 0x25 };
    const unsigned char two[] = { 0x7b, 0xbd };
    const unsigned char four[] = { 0x9d, 0x7f, 0x3e, 0x7d };
    const unsigned char eight[] = { 0xc2, 0x19, 0x7c, 0x5e, 0xff, 0x14, 0xe8, 0x8c };
    const uint64_t values[] = { 0, 63, 64, 16383, 16384, 1073741823, 1073741824, VARINT_MAX };
    unsigned char out[8];
    uint64_t value = 0;

    CHECK_EQUAL(1, varint_decode(one, sizeof(one), &value));
    CHECK_EQUAL(37, value);
    CHECK_EQUAL(2, varint_decode(two, sizeof(two), &value));
    CHECK_EQUAL(15293, value);
    CHECK_EQUAL(4, varint_decode(four, sizeof(four), &value));
    CHECK_EQUAL(494878333, value);
    CHECK_EQUAL(8, varint_decode(eight, sizeof(eight), &value));
    CHECK(value == 151288809941952652ULL);
    CHECK_EQUAL(0, varint_decode(eight, 7, &value));
    CHECK_EQUAL(0, varint_decode(eight, 0, &value));

    CHECK_EQUAL(2, varint_encode(15293, out));
    CHECK(memcmp(out, two, 2) == 0);

    for (size_t index = 0; index < sizeof(values) / sizeof(values[0]); index++) {
        size_t size = varint_encode(values[index], out);

        CHECK_EQUAL(varint_size(values[index]), size);
        CHECK_EQUAL((long)size, varint_decode(out, size, &value));
        CHECK(value == values[index]);
    }
}

static void audio_is_cut_into_one_second_groups(void) {
    ObjectTrack track;
    unsigned char payload[40];
    unsigned char copy[64];

    CHECK_EQUAL(0, object_track_init(&track, 1 << 16, 1024, 1000, 48000));

    for (int index = 0; index < 120; index++) {
        memset(payload, index, sizeof(payload));

        CHECK_EQUAL(index, object_track_append(&track, payload, sizeof(payload), (uint64_t)index * 960, 0));
    }

    CHECK_EQUAL(1002, track.group);
    CHECK_EQUAL(100, track.group_first);
    CHECK_EQUAL(96000, track.group_timestamp);
    CHECK_EQUAL(0, object_track_oldest(&track));

    const ObjectEntry *first = object_track_entry(&track, 0);
    const ObjectEntry *last_of_first = object_track_entry(&track, 49);
    const ObjectEntry *second = object_track_entry(&track, 50);
    const ObjectEntry *latest = object_track_entry(&track, 119);

    CHECK(first && first->group == 1000 && first->index == 0);
    CHECK(last_of_first && last_of_first->group == 1000 && last_of_first->index == 49);
    CHECK(second && second->group == 1001 && second->index == 0);
    CHECK(latest && latest->group == 1002 && latest->index == 19);
    CHECK(object_track_entry(&track, 120) == NULL);
    CHECK_EQUAL(40, object_track_copy(&track, latest, copy, sizeof(copy)));
    CHECK_EQUAL(119, copy[39]);
    CHECK_EQUAL(0, object_track_copy(&track, latest, copy, 10));

    object_track_free(&track);
}

static void old_objects_fall_out_of_both_rings(void) {
    ObjectTrack track;
    unsigned char payload[100];
    unsigned char copy[100];

    CHECK_EQUAL(0, object_track_init(&track, 1000, 64, 1, 0));

    for (int index = 0; index < 25; index++) {
        memset(payload, index, sizeof(payload));
        object_track_append(&track, payload, sizeof(payload), (uint64_t)index, 0);
    }

    CHECK_EQUAL(15, object_track_oldest(&track));
    CHECK(object_track_entry(&track, 14) == NULL);
    CHECK(object_track_entry(&track, 15) != NULL);
    CHECK_EQUAL(1, track.group);

    const ObjectEntry *wrapped = object_track_entry(&track, 24);

    CHECK_EQUAL(100, object_track_copy(&track, wrapped, copy, sizeof(copy)));
    CHECK_EQUAL(24, copy[0]);
    CHECK_EQUAL(24, copy[99]);

    object_track_free(&track);

    CHECK_EQUAL(0, object_track_init(&track, 1 << 16, 8, 1, 0));

    for (int index = 0; index < 20; index++) {
        object_track_append(&track, payload, 10, (uint64_t)index, 0);
    }

    CHECK_EQUAL(12, object_track_oldest(&track));

    object_track_free(&track);
}

static void groups_can_be_forced_and_cleared(void) {
    ObjectTrack track;
    const unsigned char text[] = "{\"title\":\"x\"}";

    CHECK_EQUAL(0, object_track_init(&track, 4096, 16, 7, 0));

    object_track_append(&track, text, sizeof(text) - 1, 10, 1);
    object_track_append(&track, text, sizeof(text) - 1, 20, 0);
    object_track_append(&track, text, sizeof(text) - 1, 30, 1);

    CHECK_EQUAL(8, track.group);
    CHECK_EQUAL(2, track.group_first);
    CHECK_EQUAL(1, object_track_entry(&track, 1)->index);

    object_track_clear(&track);

    CHECK_EQUAL(3, object_track_oldest(&track));
    CHECK(object_track_entry(&track, 2) == NULL);

    uint64_t next = object_track_append(&track, text, sizeof(text) - 1, 40, 0);

    CHECK_EQUAL(3, next);
    CHECK_EQUAL(9, object_track_entry(&track, next)->group);
    CHECK_EQUAL(0, object_track_entry(&track, next)->index);

    object_track_free(&track);
}

int main(void) {
    varints_follow_rfc_9000();
    audio_is_cut_into_one_second_groups();
    old_objects_fall_out_of_both_rings();
    groups_can_be_forced_and_cleared();

    CHECK_REPORT("objects");
}
