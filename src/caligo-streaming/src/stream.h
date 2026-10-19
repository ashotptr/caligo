#ifndef RELAY_STREAM_H
#define RELAY_STREAM_H

#include <stddef.h>
#include <stdint.h>

#define STREAM_TITLES 64
#define STREAM_TEXT 256

typedef struct {
    uint64_t position;
    uint64_t time_ns;
} StreamMark;

typedef struct {
    uint64_t id;
    uint64_t position;
    int is_request;
    char artist[STREAM_TEXT];
    char album[STREAM_TEXT];
    char title[STREAM_TEXT];
} StreamTitle;

typedef struct {
    unsigned char *data;
    size_t capacity;
    uint64_t head;
    StreamMark *marks;
    size_t mark_capacity;
    uint64_t mark_count;
    uint64_t time_ns;
    StreamTitle titles[STREAM_TITLES];
    uint64_t title_count;
    uint64_t title_id_base;
} Stream;

int stream_init(Stream *stream, size_t capacity);

void stream_free(Stream *stream);

void stream_append(Stream *stream, const unsigned char *frame, size_t length, uint64_t duration_ns);

void stream_clear(Stream *stream);

uint64_t stream_tail(const Stream *stream);

int stream_contains(const Stream *stream, uint64_t position);

int stream_frame_before(const Stream *stream, uint64_t position, StreamMark *mark);

uint64_t stream_start_position(const Stream *stream, size_t burst_bytes);

size_t stream_read(const Stream *stream, uint64_t position, const unsigned char **bytes);

void stream_set_title(Stream *stream, const char *artist, const char *album, const char *title, int is_request);

const StreamTitle *stream_title_at(const Stream *stream, uint64_t position);

#endif
