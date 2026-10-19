#include "media.h"

#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/sha.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>

#define MEDIA_PAYLOAD_BYTES 32
#define MEDIA_MAC_BYTES 16

static const char internal_purpose[] = "caligo-internal-v1";

static int base64url_value(char c) {
    if (c >= 'A' && c <= 'Z') {
        return c - 'A';
    }

    if (c >= 'a' && c <= 'z') {
        return c - 'a' + 26;
    }

    if (c >= '0' && c <= '9') {
        return c - '0' + 52;
    }

    if (c == '-') {
        return 62;
    }

    if (c == '_') {
        return 63;
    }

    return -1;
}

static void put_u64(unsigned char *target, uint64_t value) {
    for (int i = 7; i >= 0; i--) {
        target[i] = (unsigned char)(value & 0xff);
        value >>= 8;
    }
}

static uint64_t get_u64(const unsigned char *source) {
    uint64_t value = 0;

    for (int i = 0; i < 8; i++) {
        value = (value << 8) | source[i];
    }

    return value;
}

static int sign(const unsigned char *key, size_t key_length, const unsigned char *payload, unsigned char mac[MEDIA_MAC_BYTES]) {
    unsigned char full[EVP_MAX_MD_SIZE];
    unsigned int full_length = 0;

    if (!HMAC(EVP_sha256(), key, (int)key_length, payload, MEDIA_PAYLOAD_BYTES, full, &full_length) || full_length < MEDIA_MAC_BYTES) {
        return -1;
    }

    memcpy(mac, full, MEDIA_MAC_BYTES);

    return 0;
}

uint64_t media_scope(const char *storage_path) {
    unsigned char digest[SHA256_DIGEST_LENGTH];

    SHA256((const unsigned char *)storage_path, strlen(storage_path), digest);

    return get_u64(digest);
}

int media_token_read(const unsigned char *key, size_t key_length, const char *token, size_t token_length, MediaGrant *grant) {
    unsigned char bytes[MEDIA_TOKEN_BYTES];

    if (!token || token_length != MEDIA_TOKEN_TEXT || key_length == 0) {
        return -1;
    }

    for (size_t group = 0; group < MEDIA_TOKEN_TEXT / 4; group++) {
        uint32_t bits = 0;

        for (size_t i = 0; i < 4; i++) {
            int value = base64url_value(token[group * 4 + i]);

            if (value < 0) {
                return -1;
            }

            bits = (bits << 6) | (uint32_t)value;
        }

        bytes[group * 3] = (unsigned char)(bits >> 16);
        bytes[group * 3 + 1] = (unsigned char)(bits >> 8);
        bytes[group * 3 + 2] = (unsigned char)bits;
    }

    unsigned char expected[MEDIA_MAC_BYTES];

    if (sign(key, key_length, bytes, expected) != 0 || CRYPTO_memcmp(expected, bytes + MEDIA_PAYLOAD_BYTES, MEDIA_MAC_BYTES) != 0) {
        return -1;
    }

    uint64_t expires = get_u64(bytes + MEDIA_SESSION_BYTES);

    if (expires > (uint64_t)INT64_MAX) {
        return -1;
    }

    memcpy(grant->session, bytes, MEDIA_SESSION_BYTES);
    grant->expires = (int64_t)expires;
    grant->scope = get_u64(bytes + MEDIA_SESSION_BYTES + 8);

    return 0;
}

int media_token_write(const unsigned char *key, size_t key_length, const unsigned char session[MEDIA_SESSION_BYTES], int64_t expires, const char *storage_path, char text[MEDIA_TOKEN_TEXT + 1]) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    unsigned char bytes[MEDIA_TOKEN_BYTES];

    if (expires < 0) {
        return -1;
    }

    memcpy(bytes, session, MEDIA_SESSION_BYTES);
    put_u64(bytes + MEDIA_SESSION_BYTES, (uint64_t)expires);
    put_u64(bytes + MEDIA_SESSION_BYTES + 8, media_scope(storage_path));

    if (sign(key, key_length, bytes, bytes + MEDIA_PAYLOAD_BYTES) != 0) {
        return -1;
    }

    for (size_t group = 0; group < MEDIA_TOKEN_BYTES / 3; group++) {
        uint32_t bits = ((uint32_t)bytes[group * 3] << 16) | ((uint32_t)bytes[group * 3 + 1] << 8) | bytes[group * 3 + 2];

        for (size_t i = 0; i < 4; i++) {
            text[group * 4 + i] = alphabet[(bits >> (18 - 6 * i)) & 63];
        }
    }

    text[MEDIA_TOKEN_TEXT] = '\0';

    return 0;
}

void media_internal_key(const unsigned char *key, size_t key_length, char hex[MEDIA_INTERNAL_KEY_HEX + 1]) {
    static const char digits[] = "0123456789abcdef";
    unsigned char full[EVP_MAX_MD_SIZE];
    unsigned int full_length = 0;

    hex[0] = '\0';

    if (!HMAC(EVP_sha256(), key, (int)key_length, (const unsigned char *)internal_purpose, sizeof(internal_purpose) - 1, full, &full_length) || full_length != 32) {
        return;
    }

    for (unsigned int i = 0; i < full_length; i++) {
        hex[i * 2] = digits[full[i] >> 4];
        hex[i * 2 + 1] = digits[full[i] & 15];
    }

    hex[MEDIA_INTERNAL_KEY_HEX] = '\0';
}

void media_uuid_text(const unsigned char session[MEDIA_SESSION_BYTES], char text[MEDIA_UUID_TEXT + 1]) {
    snprintf(text, MEDIA_UUID_TEXT + 1,
             "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
             session[0], session[1], session[2], session[3], session[4], session[5], session[6], session[7],
             session[8], session[9], session[10], session[11], session[12], session[13], session[14], session[15]);
}

int media_path_safe(const char *path) {
    size_t length = path ? strlen(path) : 0;

    if (length == 0 || length > MEDIA_PATH_MAX) {
        return 0;
    }

    int segment_start = 1;

    for (size_t i = 0; i < length; i++) {
        char c = path[i];

        if (c == '/') {
            if (segment_start) {
                return 0;
            }

            segment_start = 1;

            continue;
        }

        if (segment_start && c == '.') {
            return 0;
        }

        int plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';

        if (!plain) {
            return 0;
        }

        segment_start = 0;
    }

    return !segment_start;
}

int media_file_name_plain(const char *name) {
    size_t length = name ? strlen(name) : 0;

    if (length == 0 || length > 255 || name[0] == '.' || name[0] == ' ' || name[length - 1] == ' ') {
        return 0;
    }

    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)name[i];

        if (c == '/' || c == '\\' || c == ':' || c < 0x20 || c == 0x7f) {
            return 0;
        }
    }

    return 1;
}

int media_has_extension(const char *path, const char *extension) {
    size_t length = strlen(path);
    size_t extension_length = strlen(extension);

    return length > extension_length && strcasecmp(path + length - extension_length, extension) == 0;
}

const char *media_content_type(const char *path) {
    if (media_has_extension(path, ".m3u8")) {
        return "application/vnd.apple.mpegurl";
    }

    if (media_has_extension(path, ".mpd")) {
        return "application/dash+xml";
    }

    if (media_has_extension(path, ".m4s")) {
        return "video/iso.segment";
    }

    if (media_has_extension(path, ".mp4")) {
        return "video/mp4";
    }

    if (media_has_extension(path, ".vtt")) {
        return "text/vtt; charset=utf-8";
    }

    return NULL;
}
