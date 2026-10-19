#ifndef CALIGO_MEDIA_H
#define CALIGO_MEDIA_H

#include <stddef.h>
#include <stdint.h>

#define MEDIA_TOKEN_TEXT 64
#define MEDIA_TOKEN_BYTES 48
#define MEDIA_SESSION_BYTES 16
#define MEDIA_PATH_MAX 200
#define MEDIA_INTERNAL_KEY_HEX 64
#define MEDIA_UUID_TEXT 36

typedef struct {
    unsigned char session[MEDIA_SESSION_BYTES];
    int64_t expires;
    uint64_t scope;
} MediaGrant;

int media_token_read(const unsigned char *key, size_t key_length, const char *token, size_t token_length, MediaGrant *grant);

int media_token_write(const unsigned char *key, size_t key_length, const unsigned char session[MEDIA_SESSION_BYTES], int64_t expires, const char *storage_path, char text[MEDIA_TOKEN_TEXT + 1]);

uint64_t media_scope(const char *storage_path);

void media_internal_key(const unsigned char *key, size_t key_length, char hex[MEDIA_INTERNAL_KEY_HEX + 1]);

void media_uuid_text(const unsigned char session[MEDIA_SESSION_BYTES], char text[MEDIA_UUID_TEXT + 1]);

int media_path_safe(const char *path);

int media_file_name_plain(const char *name);

const char *media_content_type(const char *path);

int media_has_extension(const char *path, const char *extension);

#endif
