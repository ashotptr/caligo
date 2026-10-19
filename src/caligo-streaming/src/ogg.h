#ifndef RELAY_OGG_H
#define RELAY_OGG_H

#include <stddef.h>
#include <stdint.h>

#define OGG_HEADER_BYTES 27
#define OGG_MAX_PAGE (OGG_HEADER_BYTES + 255 + 255 * 255)
#define OGG_CONTINUED 0x01
#define OGG_BOS 0x02
#define OGG_EOS 0x04
#define OGG_NO_GRANULE UINT64_MAX

#define OPUS_RATE 48000
#define OPUS_HEAD_MAX (21 + 255)
#define OPUS_TAGS_MAX 16384
#define OPUS_PACKET_MAX 65536
#define OPUS_TEXT 256

typedef struct {
    const unsigned char *bytes;
    size_t length;
    int flags;
    uint64_t granule;
    uint32_t serial;
    uint32_t sequence;
    int segments;
    const unsigned char *lacing;
    const unsigned char *body;
    size_t body_length;
} OggPage;

typedef void (*OggPageFn)(void *context, const OggPage *page);

typedef struct {
    unsigned char buffer[OGG_MAX_PAGE * 2];
    size_t length;
    uint64_t pages;
    uint64_t dropped_bytes;
    uint64_t crc_errors;
    OggPageFn emit;
    void *context;
} OggReader;

typedef struct {
    int version;
    int channels;
    int pre_skip;
    uint32_t input_rate;
    int gain;
    int family;
    int streams;
    int coupled;
    unsigned char mapping[255];
} OpusHead;

typedef struct {
    char artist[OPUS_TEXT];
    char album[OPUS_TEXT];
    char title[OPUS_TEXT];
} OpusTitle;

typedef enum { SPLICE_IDLE, SPLICE_TAGS, SPLICE_AUDIO } SpliceState;

typedef void (*SplicePageFn)(void *context, const unsigned char *page, size_t length, uint64_t samples);
typedef void (*SplicePacketFn)(void *context, const unsigned char *packet, size_t length, uint64_t start, int samples);
typedef void (*SpliceTitleFn)(void *context, const OpusTitle *title);
typedef void (*SpliceResetFn)(void *context);

typedef struct {
    uint32_t serial;
    uint32_t sequence;
    uint64_t granule;
    int have_head;
    OpusHead head;
    unsigned char head_packet[OPUS_HEAD_MAX];
    size_t head_length;
    SpliceState state;
    uint32_t chain_serial;
    unsigned char tags[OPUS_TAGS_MAX];
    size_t tags_length;
    int in_packet;
    int packet_known;
    unsigned char packet_start[2];
    size_t packet_start_length;
    unsigned char packet[OPUS_PACKET_MAX];
    size_t packet_length;
    int packet_overflow;
    uint64_t chains;
    uint64_t pages_in;
    uint64_t pages_out;
    uint64_t packets;
    uint64_t oversized_packets;
    uint64_t skipped_pages;
    uint64_t resets;
    unsigned char page[OGG_MAX_PAGE];
    SplicePageFn emit;
    SplicePacketFn packet_out;
    SpliceTitleFn titled;
    SpliceResetFn reset;
    void *context;
} OpusSplicer;

uint32_t ogg_page_checksum(const unsigned char *page, size_t length);

int ogg_page_parse(const unsigned char *bytes, size_t length, OggPage *page);

size_t ogg_page_build(
    unsigned char *out, size_t capacity, int flags, uint64_t granule, uint32_t serial, uint32_t sequence,
    const unsigned char *packet, size_t packet_length);

void ogg_page_rewrite(unsigned char *page, size_t length, int flags, uint64_t granule, uint32_t serial, uint32_t sequence);

void ogg_reader_init(OggReader *reader, OggPageFn emit, void *context);

void ogg_reader_reset(OggReader *reader);

void ogg_reader_feed(OggReader *reader, const unsigned char *data, size_t length);

int opus_packet_samples(const unsigned char *start, size_t available);

int opus_head_parse(const unsigned char *packet, size_t length, OpusHead *head);

int opus_head_compatible(const OpusHead *first, const OpusHead *second);

int opus_tags_parse(const unsigned char *packet, size_t length, OpusTitle *title);

size_t opus_tags_build(unsigned char *out, size_t capacity, const char *vendor, const OpusTitle *title);

void opus_splicer_init(
    OpusSplicer *splicer, uint32_t serial, SplicePageFn emit, SpliceTitleFn titled, SpliceResetFn reset, void *context);

void opus_splicer_packets(OpusSplicer *splicer, SplicePacketFn packet_out);

void opus_splicer_page(OpusSplicer *splicer, const OggPage *page);

void opus_splicer_source_ended(OpusSplicer *splicer);

size_t opus_splicer_headers(const OpusSplicer *splicer, const OpusTitle *title, unsigned char *out, size_t capacity);

#endif
