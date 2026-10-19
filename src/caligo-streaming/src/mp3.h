#ifndef RELAY_MP3_H
#define RELAY_MP3_H

#include <stddef.h>
#include <stdint.h>

#define MP3_MAX_FRAME 1441
#define MP3_FRAMER_BUFFER 65536

typedef struct {
    int version;
    int bitrate_kbps;
    int sample_rate;
    int channels;
    int samples;
    int length;
} Mp3Header;

typedef void (*Mp3FrameFn)(void *context, const unsigned char *frame, size_t length, const Mp3Header *header);

typedef struct {
    unsigned char buffer[MP3_FRAMER_BUFFER];
    size_t length;
    uint64_t skip;
    int synced;
    Mp3Header format;
    uint64_t frames;
    uint64_t dropped_bytes;
    uint64_t tag_bytes;
    uint64_t info_frames;
    uint64_t resyncs;
    uint64_t format_changes;
    Mp3FrameFn emit;
    void *context;
} Mp3Framer;

int mp3_parse_header(const unsigned char *bytes, Mp3Header *header);

int mp3_is_info_frame(const unsigned char *frame, size_t length, const Mp3Header *header);

void mp3_framer_init(Mp3Framer *framer, Mp3FrameFn emit, void *context);

void mp3_framer_reset(Mp3Framer *framer);

void mp3_framer_feed(Mp3Framer *framer, const unsigned char *data, size_t length);

#endif
