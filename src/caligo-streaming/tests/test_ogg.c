#include "check.h"
#include "ogg.h"

#include <stdlib.h>

static const unsigned char FFMPEG_HEAD_PAGE[] = {
    0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1C, 0xD5, 0xC5, 0xF7, 0x01, 0x13, 0x4F, 0x70, 0x75, 0x73,
    0x48, 0x65, 0x61, 0x64, 0x01, 0x02, 0x38, 0x01, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00
};

#define MAX_PAGES 64

typedef struct {
    unsigned char bytes[MAX_PAGES][4096];
    size_t lengths[MAX_PAGES];
    uint64_t samples[MAX_PAGES];
    int count;
    int titles;
    char last_title[OPUS_TEXT];
    char last_artist[OPUS_TEXT];
    int resets;
    size_t packet_lengths[MAX_PAGES];
    uint64_t packet_starts[MAX_PAGES];
    int packet_samples[MAX_PAGES];
    int packet_count;
} Collected;

static void collect_packet(void *context, const unsigned char *packet, size_t length, uint64_t start, int samples) {
    Collected *collected = context;

    (void)packet;

    if (collected->packet_count < MAX_PAGES) {
        collected->packet_lengths[collected->packet_count] = length;
        collected->packet_starts[collected->packet_count] = start;
        collected->packet_samples[collected->packet_count] = samples;
        collected->packet_count++;
    }
}

static void collect_page(void *context, const OggPage *page) {
    Collected *collected = context;

    if (collected->count < MAX_PAGES && page->length <= sizeof(collected->bytes[0])) {
        memcpy(collected->bytes[collected->count], page->bytes, page->length);

        collected->lengths[collected->count] = page->length;
        collected->count++;
    }
}

static void collect_spliced(void *context, const unsigned char *page, size_t length, uint64_t samples) {
    Collected *collected = context;

    if (collected->count < MAX_PAGES && length <= sizeof(collected->bytes[0])) {
        memcpy(collected->bytes[collected->count], page, length);

        collected->lengths[collected->count] = length;
        collected->samples[collected->count] = samples;
        collected->count++;
    }
}

static void collect_title(void *context, const OpusTitle *title) {
    Collected *collected = context;

    collected->titles++;

    snprintf(collected->last_title, sizeof(collected->last_title), "%s", title->title);
    snprintf(collected->last_artist, sizeof(collected->last_artist), "%s", title->artist);
}

static void collect_reset(void *context) {
    ((Collected *)context)->resets++;
}

static size_t head_packet(unsigned char *out, int channels, int pre_skip) {
    memcpy(out, "OpusHead", 8);

    out[8] = 1;
    out[9] = (unsigned char)channels;
    out[10] = (unsigned char)(pre_skip & 0xFF);
    out[11] = (unsigned char)(pre_skip >> 8);
    out[12] = 0x80;
    out[13] = 0xBB;
    out[14] = 0;
    out[15] = 0;
    out[16] = 0;
    out[17] = 0;
    out[18] = 0;

    return 19;
}

static size_t tags_packet(unsigned char *out, const char *title) {
    OpusTitle tags;

    memset(&tags, 0, sizeof(tags));
    snprintf(tags.title, sizeof(tags.title), "%s", title);
    snprintf(tags.artist, sizeof(tags.artist), "Tester");

    return opus_tags_build(out, 1024, "test encoder", &tags);
}

static size_t audio_page(
    unsigned char *out, int flags, uint64_t granule, uint32_t serial, uint32_t sequence, int packets, unsigned char toc) {
    unsigned char payload[3 * 40];

    for (int index = 0; index < packets; index++) {
        payload[index * 40] = toc;

        for (int byte = 1; byte < 40; byte++) {
            payload[index * 40 + byte] = (unsigned char)(sequence * 3 + (uint32_t)index);
        }
    }

    size_t segments = (size_t)packets;
    size_t total = OGG_HEADER_BYTES + segments + (size_t)packets * 40;

    memcpy(out, "OggS", 4);
    memset(out + 4, 0, OGG_HEADER_BYTES - 4);

    out[5] = (unsigned char)flags;
    out[26] = (unsigned char)segments;

    for (int index = 0; index < packets; index++) {
        out[OGG_HEADER_BYTES + index] = 40;
    }

    memcpy(out + OGG_HEADER_BYTES + segments, payload, (size_t)packets * 40);
    ogg_page_rewrite(out, total, flags, granule, serial, sequence);

    return total;
}

static void feed_page(OpusSplicer *splicer, const unsigned char *bytes, size_t length) {
    OggPage page;

    CHECK_EQUAL((int)length, ogg_page_parse(bytes, length, &page));

    opus_splicer_page(splicer, &page);
}

static void feed_chain(OpusSplicer *splicer, uint32_t serial, int channels, const char *title, int audio_pages) {
    unsigned char packet[1024];
    unsigned char page[4096];
    size_t length;

    length = ogg_page_build(page, sizeof(page), OGG_BOS, 0, serial, 0, packet, head_packet(packet, channels, 312));
    feed_page(splicer, page, length);

    length = ogg_page_build(page, sizeof(page), 0, 0, serial, 1, packet, tags_packet(packet, title));
    feed_page(splicer, page, length);

    for (int index = 0; index < audio_pages; index++) {
        int flags = index == audio_pages - 1 ? OGG_EOS : 0;

        length = audio_page(page, flags, 312 + (uint64_t)(index + 1) * 2880, serial, (uint32_t)(2 + index), 3, 0xF8);
        feed_page(splicer, page, length);
    }
}

static uint32_t read32(const unsigned char *bytes) {
    return (uint32_t)bytes[0] | (uint32_t)bytes[1] << 8 | (uint32_t)bytes[2] << 16 | (uint32_t)bytes[3] << 24;
}

static void the_checksum_matches_ffmpeg(void) {
    OggPage page;
    unsigned char copy[sizeof(FFMPEG_HEAD_PAGE)];

    CHECK_EQUAL(0x89A1897Fu, ogg_page_checksum((const unsigned char *)"123456789", 9));
    CHECK_EQUAL((int)sizeof(FFMPEG_HEAD_PAGE), ogg_page_parse(FFMPEG_HEAD_PAGE, sizeof(FFMPEG_HEAD_PAGE), &page));
    CHECK_EQUAL(OGG_BOS, page.flags);
    CHECK_EQUAL(0, page.granule);
    CHECK_EQUAL(1, page.segments);
    CHECK_EQUAL(19, page.body_length);

    memcpy(copy, FFMPEG_HEAD_PAGE, sizeof(copy));
    copy[40] ^= 1;

    CHECK_EQUAL(-2, ogg_page_parse(copy, sizeof(copy), &page));
    CHECK_EQUAL(0, ogg_page_parse(FFMPEG_HEAD_PAGE, 30, &page));
    CHECK_EQUAL(-1, ogg_page_parse((const unsigned char *)"OggX0000000000000000000000000000", 32, &page));
}

static void pages_are_built_and_read_back(void) {
    unsigned char packet[510];
    unsigned char out[1024];
    OggPage page;

    for (size_t index = 0; index < sizeof(packet); index++) {
        packet[index] = (unsigned char)index;
    }

    size_t length = ogg_page_build(out, sizeof(out), OGG_BOS, 77, 0xABCDEF01u, 9, packet, 300);

    CHECK_EQUAL(OGG_HEADER_BYTES + 2 + 300, length);
    CHECK_EQUAL((int)length, ogg_page_parse(out, length, &page));
    CHECK_EQUAL(OGG_BOS, page.flags);
    CHECK_EQUAL(77, page.granule);
    CHECK_EQUAL(0xABCDEF01u, page.serial);
    CHECK_EQUAL(9, page.sequence);
    CHECK_EQUAL(2, page.segments);
    CHECK_EQUAL(255, page.lacing[0]);
    CHECK_EQUAL(45, page.lacing[1]);
    CHECK(memcmp(page.body, packet, 300) == 0);

    length = ogg_page_build(out, sizeof(out), 0, 0, 1, 1, packet, 510);

    CHECK_EQUAL((int)length, ogg_page_parse(out, length, &page));
    CHECK_EQUAL(3, page.segments);
    CHECK_EQUAL(0, page.lacing[2]);
    CHECK_EQUAL(0, ogg_page_build(out, 100, 0, 0, 1, 1, packet, 300));

    ogg_page_rewrite(out, length, OGG_EOS, OGG_NO_GRANULE, 5, 6);

    CHECK_EQUAL((int)length, ogg_page_parse(out, length, &page));
    CHECK_EQUAL(OGG_EOS, page.flags);
    CHECK(page.granule == OGG_NO_GRANULE);
    CHECK_EQUAL(5, page.serial);
}

static void the_reader_finds_pages_in_noise(void) {
    static OggReader reader;
    static Collected collected;
    unsigned char stream[4096];
    unsigned char packet[200];
    size_t length = 0;

    memset(packet, 7, sizeof(packet));
    memset(&collected, 0, sizeof(collected));

    memcpy(stream, "garbage OggS more garbage", 25);
    length += 25;
    length += ogg_page_build(stream + length, sizeof(stream) - length, 0, 1, 42, 1, packet, 200);

    size_t broken = length;

    length += ogg_page_build(stream + length, sizeof(stream) - length, 0, 2, 42, 2, packet, 150);
    stream[broken + 40] ^= 0xFF;

    memcpy(stream + length, "xx", 2);
    length += 2;
    length += ogg_page_build(stream + length, sizeof(stream) - length, 0, 3, 42, 3, packet, 100);

    ogg_reader_init(&reader, collect_page, &collected);

    for (size_t index = 0; index < length; index++) {
        ogg_reader_feed(&reader, stream + index, 1);
    }

    CHECK_EQUAL(2, collected.count);
    CHECK_EQUAL(2, reader.pages);
    CHECK_EQUAL(1, reader.crc_errors);
    CHECK(reader.dropped_bytes >= 27);

    OggPage first;
    OggPage last;

    ogg_page_parse(collected.bytes[0], collected.lengths[0], &first);
    ogg_page_parse(collected.bytes[1], collected.lengths[1], &last);

    CHECK_EQUAL(1, first.sequence);
    CHECK_EQUAL(3, last.sequence);

    memset(&collected, 0, sizeof(collected));
    ogg_reader_init(&reader, collect_page, &collected);
    ogg_reader_feed(&reader, stream, length);

    CHECK_EQUAL(2, collected.count);
}

static void packet_durations_follow_the_toc(void) {
    const unsigned char celt20[] = { 0xF8 };
    const unsigned char celt2_5[] = { 0x80 };
    const unsigned char silk60[] = { 0x18 };
    const unsigned char hybrid20[] = { 0x68 };
    const unsigned char two_frames[] = { 0xF9 };
    const unsigned char three_frames[] = { 0xFB, 0x03 };
    const unsigned char no_frames[] = { 0xFB, 0x00 };
    const unsigned char too_long[] = { 0xFB, 0x07 };

    CHECK_EQUAL(960, opus_packet_samples(celt20, 1));
    CHECK_EQUAL(120, opus_packet_samples(celt2_5, 1));
    CHECK_EQUAL(2880, opus_packet_samples(silk60, 1));
    CHECK_EQUAL(960, opus_packet_samples(hybrid20, 1));
    CHECK_EQUAL(1920, opus_packet_samples(two_frames, 1));
    CHECK_EQUAL(2880, opus_packet_samples(three_frames, 2));
    CHECK_EQUAL(-1, opus_packet_samples(three_frames, 1));
    CHECK_EQUAL(-1, opus_packet_samples(no_frames, 2));
    CHECK_EQUAL(-1, opus_packet_samples(too_long, 2));
    CHECK_EQUAL(-1, opus_packet_samples(celt20, 0));
}

static void opus_heads_are_read_and_compared(void) {
    OpusHead stereo;
    OpusHead other;
    unsigned char packet[64];

    CHECK(opus_head_parse(FFMPEG_HEAD_PAGE + 28, 19, &stereo));
    CHECK_EQUAL(2, stereo.channels);
    CHECK_EQUAL(312, stereo.pre_skip);
    CHECK_EQUAL(48000, stereo.input_rate);
    CHECK_EQUAL(0, stereo.family);

    head_packet(packet, 2, 3840);

    CHECK(opus_head_parse(packet, 19, &other));
    CHECK(opus_head_compatible(&stereo, &other));

    head_packet(packet, 1, 312);

    CHECK(opus_head_parse(packet, 19, &other));
    CHECK(!opus_head_compatible(&stereo, &other));

    head_packet(packet, 2, 312);
    packet[16] = 0x00;
    packet[17] = 0xFF;

    CHECK(opus_head_parse(packet, 19, &other));
    CHECK_EQUAL(-256, other.gain);

    packet[8] = 0x10;
    CHECK(!opus_head_parse(packet, 19, &other));

    head_packet(packet, 3, 312);
    CHECK(!opus_head_parse(packet, 19, &other));

    head_packet(packet, 6, 312);
    packet[18] = 1;
    packet[19] = 4;
    packet[20] = 2;

    for (int index = 0; index < 6; index++) {
        packet[21 + index] = (unsigned char)index;
    }

    CHECK(opus_head_parse(packet, 27, &other));
    CHECK_EQUAL(4, other.streams);
    CHECK_EQUAL(2, other.coupled);
    CHECK(!opus_head_parse(packet, 26, &other));
    CHECK(!opus_head_parse((const unsigned char *)"OpusHeaX", 8, &other));
}

static void opus_tags_carry_the_title(void) {
    OpusTitle title;
    OpusTitle read;
    unsigned char packet[2048];

    memset(&title, 0, sizeof(title));
    snprintf(title.title, sizeof(title.title), "Eres Tú");
    snprintf(title.artist, sizeof(title.artist), "Carla Morrison");

    size_t length = opus_tags_build(packet, sizeof(packet), "caligo-relay", &title);

    CHECK(length > 0);
    CHECK(opus_tags_parse(packet, length, &read));
    CHECK_TEXT("Eres Tú", read.title);
    CHECK_TEXT("Carla Morrison", read.artist);
    CHECK_TEXT("", read.album);
    CHECK_EQUAL(2, read32(packet + 8 + 4 + 12));

    unsigned char manual[128];
    size_t position = 0;

    memcpy(manual, "OpusTags", 8);
    position = 8;
    manual[position++] = 3;
    manual[position++] = 0;
    manual[position++] = 0;
    manual[position++] = 0;
    memcpy(manual + position, "enc", 3);
    position += 3;
    manual[position++] = 2;
    manual[position++] = 0;
    manual[position++] = 0;
    manual[position++] = 0;
    manual[position++] = 11;
    manual[position++] = 0;
    manual[position++] = 0;
    manual[position++] = 0;
    memcpy(manual + position, "title=A\tB\nC", 11);
    position += 11;
    manual[position++] = 200;
    manual[position++] = 0;
    manual[position++] = 0;
    manual[position++] = 0;
    memcpy(manual + position, "ARTIST=cut", 10);
    position += 10;

    CHECK(opus_tags_parse(manual, position, &read));
    CHECK_TEXT("A B C", read.title);
    CHECK_TEXT("", read.artist);
    CHECK(!opus_tags_parse((const unsigned char *)"OpusHead........", 16, &read));
    CHECK(opus_tags_parse(manual, 18, &read));
    CHECK_TEXT("", read.title);
}

static void chains_are_spliced_into_one_stream(void) {
    static OpusSplicer splicer;
    static Collected collected;

    memset(&collected, 0, sizeof(collected));
    opus_splicer_init(&splicer, 0x5EED, collect_spliced, collect_title, collect_reset, &collected);
    opus_splicer_packets(&splicer, collect_packet);

    feed_chain(&splicer, 111, 2, "One", 3);
    feed_chain(&splicer, 222, 2, "Two", 2);

    CHECK_EQUAL(15, collected.packet_count);

    for (int index = 0; index < collected.packet_count; index++) {
        CHECK_EQUAL(40, collected.packet_lengths[index]);
        CHECK_EQUAL((uint64_t)index * 960, collected.packet_starts[index]);
        CHECK_EQUAL(960, collected.packet_samples[index]);
    }

    CHECK_EQUAL(5, collected.count);
    CHECK_EQUAL(2, collected.titles);
    CHECK_TEXT("Two", collected.last_title);
    CHECK_TEXT("Tester", collected.last_artist);
    CHECK_EQUAL(0, collected.resets);
    CHECK_EQUAL(2, splicer.chains);

    for (int index = 0; index < collected.count; index++) {
        OggPage page;

        CHECK_EQUAL((int)collected.lengths[index], ogg_page_parse(collected.bytes[index], collected.lengths[index], &page));
        CHECK_EQUAL(0x5EED, page.serial);
        CHECK_EQUAL(2 + index, page.sequence);
        CHECK_EQUAL(0, page.flags);
        CHECK_EQUAL((uint64_t)(index + 1) * 2880, page.granule);
        CHECK_EQUAL(2880, collected.samples[index]);
    }

    unsigned char headers[2048];
    OpusTitle now;
    OggPage head;
    OggPage tags;
    OpusTitle read;

    memset(&now, 0, sizeof(now));
    snprintf(now.title, sizeof(now.title), "Now");

    size_t length = opus_splicer_headers(&splicer, &now, headers, sizeof(headers));
    int first = ogg_page_parse(headers, length, &head);

    CHECK(first > 0);
    CHECK_EQUAL(OGG_BOS, head.flags);
    CHECK_EQUAL(0x5EED, head.serial);
    CHECK_EQUAL(0, head.sequence);
    CHECK(memcmp(head.body, "OpusHead", 8) == 0);
    CHECK_EQUAL((int)(length - (size_t)first), ogg_page_parse(headers + first, length - (size_t)first, &tags));
    CHECK_EQUAL(1, tags.sequence);
    CHECK(opus_tags_parse(tags.body, tags.body_length, &read));
    CHECK_TEXT("Now", read.title);
    CHECK_EQUAL(0, opus_splicer_headers(&splicer, &now, headers, 40));

    feed_chain(&splicer, 333, 1, "Mono", 1);

    CHECK_EQUAL(1, collected.resets);
    CHECK_EQUAL(1, splicer.head.channels);
}

static void packets_that_cross_pages_are_timed(void) {
    static OpusSplicer splicer;
    static Collected collected;
    unsigned char packet[1024];
    unsigned char page[4096];
    unsigned char big[600];
    size_t length;
    OggPage parsed;

    memset(&collected, 0, sizeof(collected));
    opus_splicer_init(&splicer, 9, collect_spliced, NULL, NULL, &collected);
    opus_splicer_packets(&splicer, collect_packet);

    length = ogg_page_build(page, sizeof(page), OGG_BOS, 0, 50, 0, packet, head_packet(packet, 2, 312));
    feed_page(&splicer, page, length);
    length = ogg_page_build(page, sizeof(page), 0, 0, 50, 1, packet, tags_packet(packet, "Split"));
    feed_page(&splicer, page, length);

    memset(big, 0x55, sizeof(big));
    big[0] = 0xFB;
    big[1] = 0x02;

    memcpy(page, "OggS", 4);
    memset(page + 4, 0, OGG_HEADER_BYTES - 4);
    page[26] = 2;
    page[27] = 255;
    page[28] = 255;
    memcpy(page + 29, big, 510);
    ogg_page_rewrite(page, 29 + 510, 0, OGG_NO_GRANULE, 50, 2);
    feed_page(&splicer, page, 29 + 510);

    page[26] = 1;
    page[27] = 90;
    memcpy(page + 28, big + 510, 90);
    ogg_page_rewrite(page, 28 + 90, OGG_CONTINUED, 312 + 1920, 50, 3);
    feed_page(&splicer, page, 28 + 90);

    length = audio_page(page, 0, 0, 77, 4, 3, 0xF8);
    feed_page(&splicer, page, length);

    CHECK_EQUAL(2, collected.count);
    CHECK_EQUAL(1, splicer.skipped_pages);

    ogg_page_parse(collected.bytes[0], collected.lengths[0], &parsed);
    CHECK(parsed.granule == OGG_NO_GRANULE);
    CHECK_EQUAL(0, collected.samples[0]);

    ogg_page_parse(collected.bytes[1], collected.lengths[1], &parsed);
    CHECK_EQUAL(OGG_CONTINUED, parsed.flags);
    CHECK_EQUAL(1920, parsed.granule);
    CHECK_EQUAL(1920, collected.samples[1]);
    CHECK_EQUAL(1, collected.packet_count);
    CHECK_EQUAL(600, collected.packet_lengths[0]);
    CHECK_EQUAL(0, collected.packet_starts[0]);
    CHECK_EQUAL(1920, collected.packet_samples[0]);

    opus_splicer_source_ended(&splicer);

    length = audio_page(page, 0, 0, 50, 5, 3, 0xF8);
    feed_page(&splicer, page, length);

    CHECK_EQUAL(2, collected.count);
    CHECK_EQUAL(2, splicer.skipped_pages);

    length = ogg_page_build(page, sizeof(page), OGG_BOS, 0, 60, 0, packet, 8);
    feed_page(&splicer, page, length);

    CHECK_EQUAL(3, splicer.skipped_pages);
    CHECK_EQUAL(SPLICE_IDLE, splicer.state);
}

int main(void) {
    the_checksum_matches_ffmpeg();
    pages_are_built_and_read_back();
    the_reader_finds_pages_in_noise();
    packet_durations_follow_the_toc();
    opus_heads_are_read_and_compared();
    opus_tags_carry_the_title();
    chains_are_spliced_into_one_stream();
    packets_that_cross_pages_are_timed();

    CHECK_REPORT("ogg");
}
