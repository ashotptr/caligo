#ifndef RELAY_HTTP_H
#define RELAY_HTTP_H

#include <stddef.h>
#include <stdint.h>

#define HTTP_TARGET_MAX 1024

typedef struct {
    char method[8];
    char target[HTTP_TARGET_MAX];
    char path[HTTP_TARGET_MAX];
    int has_query;
    int version_minor;
    int has_range;
    uint64_t range_start;
    int wants_icy;
    int wants_events;
} HttpRequest;

typedef enum {
    CHUNK_SIZE,
    CHUNK_EXTENSION,
    CHUNK_SIZE_LF,
    CHUNK_DATA,
    CHUNK_DATA_CR,
    CHUNK_DATA_LF,
    CHUNK_DONE,
    CHUNK_ERROR
} ChunkState;

typedef struct {
    ChunkState state;
    uint64_t remaining;
    int digits;
} ChunkDecoder;

typedef void (*ChunkDataFn)(void *context, const unsigned char *data, size_t length);

typedef enum { RANGE_NONE, RANGE_PARTIAL, RANGE_UNSATISFIABLE } RangeResult;

typedef struct {
    char playing[512];
    char artist[256];
    char album[256];
    char title[256];
    int is_request;
} RadioEvent;

int http_request_complete(const char *buffer, size_t length);

int http_parse_request(const char *buffer, size_t length, HttpRequest *request);

int http_header_value(const char *headers, const char *name, char *value, size_t capacity);

void chunk_decoder_init(ChunkDecoder *decoder);

int chunk_decoder_feed(ChunkDecoder *decoder, const unsigned char *data, size_t length, ChunkDataFn emit, void *context);

int radio_event_parse(const char *data, RadioEvent *event);

size_t json_escape(char *target, size_t capacity, const char *source);

uint64_t hash_text(const char *text);

RangeResult http_byte_range(const char *value, uint64_t size, uint64_t *start, uint64_t *length);

int http_basic_credentials(const char *value, char *user, size_t user_capacity, char *password, size_t password_capacity);

int http_query_value(const char *target, const char *name, char *value, size_t capacity);

void text_utf8_clean(const char *source, char *target, size_t capacity);

int text_equal_secret(const char *given, const char *expected);

#endif
