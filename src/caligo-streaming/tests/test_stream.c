#include "check.h"
#include "stream.h"

#include <stdlib.h>

#define FRAME_NS 26122449ULL

static void fill(unsigned char *frame, size_t length, unsigned char mark) {
    memset(frame, mark, length);

    frame[0] = 0xFF;
}

static void capacity_must_be_a_power_of_two(void) {
    Stream stream;

    CHECK_EQUAL(-1, stream_init(&stream, 100000));
    CHECK_EQUAL(-1, stream_init(&stream, 1024));
    CHECK_EQUAL(0, stream_init(&stream, 65536));

    stream_free(&stream);
}

static void an_empty_stream_has_nothing_to_read(void) {
    Stream stream;
    const unsigned char *bytes;
    StreamMark mark;

    stream_init(&stream, 65536);

    CHECK_EQUAL(0, stream_read(&stream, 0, &bytes));
    CHECK_EQUAL(0, stream_start_position(&stream, 4096));
    CHECK(!stream_frame_before(&stream, 0, &mark));
    CHECK(stream_contains(&stream, 0));
    CHECK(stream_title_at(&stream, 0) == NULL);

    stream_free(&stream);
}

static void frames_are_read_back_in_order(void) {
    Stream stream;
    unsigned char frame[417];
    const unsigned char *bytes;

    stream_init(&stream, 65536);

    for (int index = 0; index < 10; index++) {
        fill(frame, sizeof(frame), (unsigned char)(index + 1));
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    CHECK_EQUAL(4170, stream.head);
    CHECK_EQUAL(0, stream_tail(&stream));
    CHECK_EQUAL(10 * FRAME_NS, stream.time_ns);

    size_t available = stream_read(&stream, 417 * 3, &bytes);

    CHECK_EQUAL(417 * 7, available);
    CHECK_EQUAL(0xFF, bytes[0]);
    CHECK_EQUAL(4, bytes[1]);
    CHECK_EQUAL(0, stream_read(&stream, stream.head, &bytes));

    stream_free(&stream);
}

static void a_listener_starts_on_a_frame_boundary(void) {
    Stream stream;
    unsigned char frame[417];
    StreamMark mark;

    stream_init(&stream, 65536);

    for (int index = 0; index < 100; index++) {
        fill(frame, sizeof(frame), 1);
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    uint64_t start = stream_start_position(&stream, 10000);

    CHECK_EQUAL(0, start % 417);
    CHECK(stream.head - start >= 10000);
    CHECK(stream.head - start < 10000 + 417);

    CHECK_EQUAL(0, stream_start_position(&stream, 1000000));
    CHECK_EQUAL(417 * 99, stream_start_position(&stream, 0));

    CHECK(stream_frame_before(&stream, 417 * 5 + 100, &mark));
    CHECK_EQUAL(417 * 5, mark.position);
    CHECK_EQUAL(5 * FRAME_NS, mark.time_ns);

    CHECK(stream_frame_before(&stream, 417 * 5, &mark));
    CHECK_EQUAL(417 * 5, mark.position);

    stream_free(&stream);
}

static void old_data_falls_out_and_reads_wrap(void) {
    Stream stream;
    unsigned char frame[417];
    const unsigned char *bytes;
    StreamMark mark;

    stream_init(&stream, 65536);

    for (int index = 0; index < 400; index++) {
        fill(frame, sizeof(frame), (unsigned char)(index % 200 + 1));
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    uint64_t head = 400ULL * 417;
    uint64_t tail = stream_tail(&stream);

    CHECK_EQUAL(head, stream.head);
    CHECK_EQUAL(head - 65536, tail);
    CHECK(!stream_contains(&stream, tail - 1));
    CHECK(stream_contains(&stream, tail));
    CHECK_EQUAL(0, stream_read(&stream, tail - 1, &bytes));

    CHECK(stream_frame_before(&stream, 0, &mark));
    CHECK(mark.position >= tail);
    CHECK(mark.position < tail + 417);
    CHECK_EQUAL(0, mark.position % 417);

    uint64_t position = mark.position;
    uint64_t total = 0;
    int broken = 0;

    while (position < stream.head) {
        size_t available = stream_read(&stream, position, &bytes);

        if (available == 0) {
            broken = 1;

            break;
        }

        for (size_t index = 0; index < available; index++) {
            uint64_t absolute = position + index;
            unsigned char expected = absolute % 417 == 0 ? 0xFF : (unsigned char)((absolute / 417) % 200 + 1);

            if (bytes[index] != expected) {
                broken = 1;
            }
        }

        position += available;
        total += available;
    }

    CHECK(!broken);
    CHECK_EQUAL(stream.head - mark.position, total);

    stream_free(&stream);
}

static void many_small_frames_are_limited_by_the_index(void) {
    Stream stream;
    unsigned char frame[30];
    StreamMark mark;

    stream_init(&stream, 65536);

    for (int index = 0; index < 3000; index++) {
        fill(frame, sizeof(frame), 2);
        stream_append(&stream, frame, sizeof(frame), 1000);
    }

    uint64_t tail = stream_tail(&stream);

    CHECK(tail > stream.head - 65536);
    CHECK_EQUAL(0, tail % 30);
    CHECK(stream_frame_before(&stream, 0, &mark));
    CHECK_EQUAL(tail, mark.position);

    stream_free(&stream);
}

static void titles_follow_the_position(void) {
    Stream stream;
    unsigned char frame[417];
    char long_text[600];

    stream_init(&stream, 65536);

    fill(frame, sizeof(frame), 1);

    stream_set_title(&stream, "First Artist", "Album", "First", 0);

    for (int index = 0; index < 10; index++) {
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    stream_set_title(&stream, "Second Artist", "Album", "Second", 1);

    for (int index = 0; index < 10; index++) {
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    CHECK_TEXT("First", stream_title_at(&stream, 0)->title);
    CHECK_TEXT("First", stream_title_at(&stream, 4169)->title);
    CHECK_TEXT("Second", stream_title_at(&stream, 4170)->title);
    CHECK_TEXT("Second", stream_title_at(&stream, stream.head)->title);
    CHECK_EQUAL(1, stream_title_at(&stream, stream.head)->is_request);
    CHECK_EQUAL(2, stream_title_at(&stream, stream.head)->id);

    memset(long_text, 'a', sizeof(long_text) - 1);

    long_text[sizeof(long_text) - 1] = '\0';
    long_text[254] = (char)0xC3;
    long_text[255] = (char)0xA9;

    stream_set_title(&stream, long_text, NULL, long_text, 0);

    const StreamTitle *cut = stream_title_at(&stream, stream.head);

    CHECK_EQUAL(254, strlen(cut->title));
    CHECK_TEXT("", cut->album);

    for (int index = 0; index < 200; index++) {
        char name[32];

        snprintf(name, sizeof(name), "Track %d", index);

        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
        stream_set_title(&stream, "Artist", "Album", name, 0);
    }

    CHECK_TEXT("Track 199", stream_title_at(&stream, stream.head)->title);
    CHECK_TEXT("Track 136", stream_title_at(&stream, 0)->title);

    stream_free(&stream);
}

static void a_cleared_stream_keeps_counting_from_its_head(void) {
    Stream stream;
    unsigned char frame[417];
    const unsigned char *bytes;
    StreamMark mark;

    stream_init(&stream, 65536);
    stream.title_id_base = 1000;

    fill(frame, sizeof(frame), 3);

    for (int index = 0; index < 20; index++) {
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    stream_set_title(&stream, "Artist", "Album", "Before", 0);
    stream_clear(&stream);

    uint64_t head = stream.head;

    CHECK_EQUAL(head, stream_tail(&stream));
    CHECK_EQUAL(0, stream_read(&stream, head - 417, &bytes));
    CHECK(!stream_frame_before(&stream, head, &mark));
    CHECK_EQUAL(head, stream_start_position(&stream, 4096));
    CHECK_EQUAL(1001, stream_title_at(&stream, head)->id);

    for (int index = 0; index < 5; index++) {
        stream_append(&stream, frame, sizeof(frame), FRAME_NS);
    }

    CHECK_EQUAL(head, stream_start_position(&stream, 100000));
    CHECK_EQUAL(head, stream_tail(&stream));
    CHECK_EQUAL(417 * 5, stream_read(&stream, head, &bytes));
    CHECK_EQUAL(25 * FRAME_NS, stream.time_ns);

    stream_free(&stream);
}

int main(void) {
    capacity_must_be_a_power_of_two();
    a_cleared_stream_keeps_counting_from_its_head();
    an_empty_stream_has_nothing_to_read();
    frames_are_read_back_in_order();
    a_listener_starts_on_a_frame_boundary();
    old_data_falls_out_and_reads_wrap();
    many_small_frames_are_limited_by_the_index();
    titles_follow_the_position();

    CHECK_REPORT("stream");
}
