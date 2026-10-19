#include "check.h"
#include "mp3.h"

#include <stdint.h>
#include <stdlib.h>

#define COLLECT_MAX (1024 * 1024)

static unsigned char collected[COLLECT_MAX];
static size_t collected_length;
static int collected_frames;
static int boundary_errors;

static void collect(void *context, const unsigned char *frame, size_t length, const Mp3Header *header) {
    (void)context;

    if (frame[0] != 0xFF || (size_t)header->length != length) {
        boundary_errors++;
    }

    if (collected_length + length <= COLLECT_MAX) {
        memcpy(collected + collected_length, frame, length);

        collected_length += length;
    }

    collected_frames++;
}

static void reset_collection(void) {
    collected_length = 0;
    collected_frames = 0;
    boundary_errors = 0;
}

static uint32_t seed = 12345;

static unsigned char filler(void) {
    seed = seed * 1103515245u + 12345u;

    unsigned char byte = (unsigned char)(seed >> 16);

    return byte == 0xFF ? 0x7F : byte;
}

static size_t make_frame(unsigned char *target, int padded, unsigned char mark) {
    size_t length = padded ? 418 : 417;

    target[0] = 0xFF;
    target[1] = 0xFB;
    target[2] = padded ? 0x92 : 0x90;
    target[3] = 0x00;

    for (size_t index = 4; index < length; index++) {
        target[index] = filler();
    }

    target[4] = mark;

    return length;
}

static size_t make_frames(unsigned char *target, int count, unsigned char first_mark) {
    size_t length = 0;

    for (int index = 0; index < count; index++) {
        length += make_frame(target + length, index % 3 == 0, (unsigned char)(first_mark + index));
    }

    return length;
}

static size_t make_id3v2(unsigned char *target, size_t body) {
    target[0] = 'I';
    target[1] = 'D';
    target[2] = '3';
    target[3] = 3;
    target[4] = 0;
    target[5] = 0;
    target[6] = (unsigned char)((body >> 21) & 0x7F);
    target[7] = (unsigned char)((body >> 14) & 0x7F);
    target[8] = (unsigned char)((body >> 7) & 0x7F);
    target[9] = (unsigned char)(body & 0x7F);

    for (size_t index = 0; index < body; index += 4) {
        target[10 + index] = 0xFF;

        if (index + 1 < body) target[10 + index + 1] = 0xFB;
        if (index + 2 < body) target[10 + index + 2] = 0x90;
        if (index + 3 < body) target[10 + index + 3] = 0x00;
    }

    return body + 10;
}

static void feed_in_pieces(Mp3Framer *framer, const unsigned char *data, size_t length, size_t piece) {
    for (size_t offset = 0; offset < length; offset += piece) {
        size_t take = length - offset < piece ? length - offset : piece;

        mp3_framer_feed(framer, data + offset, take);
    }
}

static void headers_are_read(void) {
    Mp3Header header;
    unsigned char stereo[4] = { 0xFF, 0xFB, 0x90, 0x00 };
    unsigned char padded[4] = { 0xFF, 0xFB, 0x92, 0x40 };
    unsigned char mpeg2_mono[4] = { 0xFF, 0xF3, 0x40, 0xC0 };
    unsigned char mpeg25[4] = { 0xFF, 0xE3, 0x80, 0x00 };

    CHECK(mp3_parse_header(stereo, &header));
    CHECK_EQUAL(1, header.version);
    CHECK_EQUAL(128, header.bitrate_kbps);
    CHECK_EQUAL(44100, header.sample_rate);
    CHECK_EQUAL(2, header.channels);
    CHECK_EQUAL(1152, header.samples);
    CHECK_EQUAL(417, header.length);

    CHECK(mp3_parse_header(padded, &header));
    CHECK_EQUAL(418, header.length);

    CHECK(mp3_parse_header(mpeg2_mono, &header));
    CHECK_EQUAL(2, header.version);
    CHECK_EQUAL(32, header.bitrate_kbps);
    CHECK_EQUAL(22050, header.sample_rate);
    CHECK_EQUAL(1, header.channels);
    CHECK_EQUAL(576, header.samples);
    CHECK_EQUAL(104, header.length);

    CHECK(mp3_parse_header(mpeg25, &header));
    CHECK_EQUAL(25, header.version);
    CHECK_EQUAL(11025, header.sample_rate);
}

static void impossible_headers_are_refused(void) {
    Mp3Header header;
    unsigned char free_format[4] = { 0xFF, 0xFB, 0x00, 0x00 };
    unsigned char bad_bitrate[4] = { 0xFF, 0xFB, 0xF0, 0x00 };
    unsigned char bad_rate[4] = { 0xFF, 0xFB, 0x9C, 0x00 };
    unsigned char reserved_version[4] = { 0xFF, 0xEB, 0x90, 0x00 };
    unsigned char layer_one[4] = { 0xFF, 0xFF, 0x90, 0x00 };
    unsigned char reserved_emphasis[4] = { 0xFF, 0xFB, 0x90, 0x02 };
    unsigned char no_sync[4] = { 0xFE, 0xFB, 0x90, 0x00 };

    CHECK(!mp3_parse_header(free_format, &header));
    CHECK(!mp3_parse_header(bad_bitrate, &header));
    CHECK(!mp3_parse_header(bad_rate, &header));
    CHECK(!mp3_parse_header(reserved_version, &header));
    CHECK(!mp3_parse_header(layer_one, &header));
    CHECK(!mp3_parse_header(reserved_emphasis, &header));
    CHECK(!mp3_parse_header(no_sync, &header));
}

static void frames_pass_through_whole_or_byte_by_byte(void) {
    static unsigned char input[64 * 418];
    static Mp3Framer framer;
    size_t length = make_frames(input, 64, 1);

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    mp3_framer_feed(&framer, input, length);

    CHECK_EQUAL(64, collected_frames);
    CHECK_EQUAL(length, collected_length);
    CHECK(memcmp(input, collected, length) == 0);
    CHECK_EQUAL(0, framer.dropped_bytes);
    CHECK_EQUAL(0, boundary_errors);

    for (size_t piece = 1; piece <= 5000; piece = piece * 7 + 1) {
        reset_collection();
        mp3_framer_init(&framer, collect, NULL);
        feed_in_pieces(&framer, input, length, piece);

        CHECK_EQUAL(64, collected_frames);
        CHECK(collected_length == length && memcmp(input, collected, length) == 0);
    }
}

static void joining_in_the_middle_starts_at_the_next_frame(void) {
    static unsigned char input[32 * 418];
    static Mp3Framer framer;
    size_t length = make_frames(input, 32, 1);
    size_t first = 418;
    size_t cut = 150;

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    feed_in_pieces(&framer, input + cut, length - cut, 311);

    CHECK_EQUAL(31, collected_frames);
    CHECK_EQUAL(first - cut, framer.dropped_bytes);
    CHECK(memcmp(input + first, collected, length - first) == 0);
    CHECK_EQUAL(0, boundary_errors);
}

static void tags_between_tracks_are_dropped(void) {
    static unsigned char input[400000];
    static unsigned char expected[64 * 418];
    static Mp3Framer framer;
    size_t length = 0;
    size_t expected_length = 0;
    size_t part;

    part = make_frames(input + length, 10, 1);
    memcpy(expected + expected_length, input + length, part);
    length += part;
    expected_length += part;

    memcpy(input + length, "TAG", 3);
    memset(input + length + 3, 'x', 125);
    length += 128;

    size_t small_tag = make_id3v2(input + length, 3000);
    length += small_tag;

    part = make_frames(input + length, 10, 50);
    memcpy(expected + expected_length, input + length, part);
    length += part;
    expected_length += part;

    size_t big_tag = make_id3v2(input + length, 200000);
    length += big_tag;

    part = make_frames(input + length, 10, 100);
    memcpy(expected + expected_length, input + length, part);
    length += part;
    expected_length += part;

    for (size_t piece = 1; piece <= 70000; piece = piece * 11 + 3) {
        reset_collection();
        mp3_framer_init(&framer, collect, NULL);
        feed_in_pieces(&framer, input, length, piece);

        CHECK_EQUAL(30, collected_frames);
        CHECK(collected_length == expected_length && memcmp(expected, collected, expected_length) == 0);
        CHECK_EQUAL(128 + small_tag + big_tag, framer.tag_bytes);
        CHECK_EQUAL(0, framer.dropped_bytes);
        CHECK_EQUAL(0, boundary_errors);
    }
}

static void a_tag_at_the_very_start_is_dropped(void) {
    static unsigned char input[20000];
    static Mp3Framer framer;
    size_t tag = make_id3v2(input, 5000);
    size_t frames = make_frames(input + tag, 8, 1);

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    feed_in_pieces(&framer, input, tag + frames, 97);

    CHECK_EQUAL(8, collected_frames);
    CHECK(collected_length == frames && memcmp(input + tag, collected, frames) == 0);
    CHECK_EQUAL(0, framer.dropped_bytes);
}

static void an_info_frame_is_not_audio(void) {
    static unsigned char input[8 * 418];
    static Mp3Framer framer;
    size_t first = make_frame(input, 0, 1);

    memset(input + 4, 0, first - 4);
    memcpy(input + 36, "Xing", 4);

    size_t rest = make_frames(input + first, 5, 10);

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    mp3_framer_feed(&framer, input, first + rest);

    CHECK_EQUAL(5, collected_frames);
    CHECK_EQUAL(1, framer.info_frames);
    CHECK(collected_length == rest && memcmp(input + first, collected, rest) == 0);
}

static void noise_is_skipped_and_counted(void) {
    static unsigned char input[40000];
    static Mp3Framer framer;
    size_t noise = 5000;

    for (size_t index = 0; index < noise; index++) {
        input[index] = index % 97 == 0 ? 0xFF : filler();
    }

    size_t frames = make_frames(input + noise, 12, 1);

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    feed_in_pieces(&framer, input, noise + frames, 733);

    CHECK_EQUAL(12, collected_frames);
    CHECK_EQUAL(noise, framer.dropped_bytes);
    CHECK(collected_length == frames && memcmp(input + noise, collected, frames) == 0);
}

static void a_reset_finds_the_stream_again(void) {
    static unsigned char input[20 * 418];
    static Mp3Framer framer;
    size_t length = make_frames(input, 20, 1);

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    mp3_framer_feed(&framer, input, 2000);
    mp3_framer_reset(&framer);

    int before = collected_frames;

    mp3_framer_feed(&framer, input + 3000, length - 3000);

    CHECK(before >= 3);
    CHECK(collected_frames > before);
    CHECK_EQUAL(0, boundary_errors);
    CHECK_EQUAL(2, framer.resyncs);
}

static void a_change_of_format_is_counted(void) {
    static unsigned char input[4000];
    static Mp3Framer framer;
    size_t length = make_frames(input, 3, 1);

    for (int index = 0; index < 4; index++) {
        unsigned char *frame = input + length;

        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x94;
        frame[3] = 0x00;

        memset(frame + 4, 0x11, 384 - 4);

        length += 384;
    }

    reset_collection();
    mp3_framer_init(&framer, collect, NULL);
    mp3_framer_feed(&framer, input, length);

    CHECK_EQUAL(7, collected_frames);
    CHECK_EQUAL(1, framer.format_changes);
    CHECK_EQUAL(48000, framer.format.sample_rate);
}

int main(void) {
    headers_are_read();
    impossible_headers_are_refused();
    frames_pass_through_whole_or_byte_by_byte();
    joining_in_the_middle_starts_at_the_next_frame();
    tags_between_tracks_are_dropped();
    a_tag_at_the_very_start_is_dropped();
    an_info_frame_is_not_audio();
    noise_is_skipped_and_counted();
    a_reset_finds_the_stream_again();
    a_change_of_format_is_counted();

    CHECK_REPORT("mp3");
}
