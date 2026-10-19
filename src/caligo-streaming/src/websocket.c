#include "websocket.h"

#include <string.h>
#include <strings.h>

static uint32_t rotate(uint32_t value, int bits) {
    return value << bits | value >> (32 - bits);
}

static void sha1_block(uint32_t state[5], const unsigned char block[64]) {
    uint32_t words[80];

    for (int index = 0; index < 16; index++) {
        words[index] = (uint32_t)block[index * 4] << 24 | (uint32_t)block[index * 4 + 1] << 16 |
            (uint32_t)block[index * 4 + 2] << 8 | (uint32_t)block[index * 4 + 3];
    }

    for (int index = 16; index < 80; index++) {
        words[index] = rotate(words[index - 3] ^ words[index - 8] ^ words[index - 14] ^ words[index - 16], 1);
    }

    uint32_t a = state[0];
    uint32_t b = state[1];
    uint32_t c = state[2];
    uint32_t d = state[3];
    uint32_t e = state[4];

    for (int index = 0; index < 80; index++) {
        uint32_t mix;
        uint32_t constant;

        if (index < 20) {
            mix = (b & c) | (~b & d);
            constant = 0x5A827999u;
        }
        else if (index < 40) {
            mix = b ^ c ^ d;
            constant = 0x6ED9EBA1u;
        }
        else if (index < 60) {
            mix = (b & c) | (b & d) | (c & d);
            constant = 0x8F1BBCDCu;
        }
        else {
            mix = b ^ c ^ d;
            constant = 0xCA62C1D6u;
        }

        uint32_t next = rotate(a, 5) + mix + e + constant + words[index];

        e = d;
        d = c;
        c = rotate(b, 30);
        b = a;
        a = next;
    }

    state[0] += a;
    state[1] += b;
    state[2] += c;
    state[3] += d;
    state[4] += e;
}

void sha1(const unsigned char *data, size_t length, unsigned char digest[20]) {
    uint32_t state[5] = { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u };
    unsigned char block[64];
    size_t offset = 0;

    while (length - offset >= 64) {
        sha1_block(state, data + offset);

        offset += 64;
    }

    size_t rest = length - offset;
    uint64_t bits = (uint64_t)length * 8;

    memset(block, 0, sizeof(block));
    memcpy(block, data + offset, rest);

    block[rest] = 0x80;

    if (rest >= 56) {
        sha1_block(state, block);
        memset(block, 0, sizeof(block));
    }

    for (int index = 0; index < 8; index++) {
        block[56 + index] = (unsigned char)(bits >> (56 - 8 * index));
    }

    sha1_block(state, block);

    for (int index = 0; index < 5; index++) {
        digest[index * 4] = (unsigned char)(state[index] >> 24);
        digest[index * 4 + 1] = (unsigned char)(state[index] >> 16);
        digest[index * 4 + 2] = (unsigned char)(state[index] >> 8);
        digest[index * 4 + 3] = (unsigned char)state[index];
    }
}

size_t base64_encode(const unsigned char *data, size_t length, char *out, size_t capacity) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t needed = (length + 2) / 3 * 4;
    size_t written = 0;

    if (capacity < needed + 1) {
        return 0;
    }

    for (size_t index = 0; index < length; index += 3) {
        uint32_t value = (uint32_t)data[index] << 16;

        if (index + 1 < length) {
            value |= (uint32_t)data[index + 1] << 8;
        }

        if (index + 2 < length) {
            value |= data[index + 2];
        }

        out[written++] = alphabet[value >> 18 & 0x3F];
        out[written++] = alphabet[value >> 12 & 0x3F];
        out[written++] = index + 1 < length ? alphabet[value >> 6 & 0x3F] : '=';
        out[written++] = index + 2 < length ? alphabet[value & 0x3F] : '=';
    }

    out[written] = '\0';

    return written;
}

int ws_accept_key(const char *key, char *out, size_t capacity) {
    static const char guid[] = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    unsigned char joined[128];
    unsigned char digest[20];
    size_t key_length = strlen(key);

    if (key_length != 24 || key[22] != '=' || key[23] != '=') {
        return 0;
    }

    for (size_t index = 0; index < 22; index++) {
        char byte = key[index];

        if (!((byte >= 'A' && byte <= 'Z') || (byte >= 'a' && byte <= 'z') || (byte >= '0' && byte <= '9') ||
              byte == '+' || byte == '/')) {
            return 0;
        }
    }

    memcpy(joined, key, key_length);
    memcpy(joined + key_length, guid, sizeof(guid) - 1);

    sha1(joined, key_length + sizeof(guid) - 1, digest);

    return base64_encode(digest, sizeof(digest), out, capacity) == 28;
}

long ws_parse_frame(unsigned char *data, size_t length, uint64_t max_payload, WsFrame *frame) {
    if (length < 2) {
        return 0;
    }

    int fin = data[0] >> 7;
    int opcode = data[0] & 0x0F;
    uint64_t payload = data[1] & 0x7F;
    size_t position = 2;

    if ((data[0] & 0x70) != 0 || (data[1] & 0x80) == 0) {
        return WS_ERROR_PROTOCOL;
    }

    if ((opcode > WS_BINARY && opcode < WS_CLOSE) || opcode > WS_PONG) {
        return WS_ERROR_PROTOCOL;
    }

    if (payload == 126) {
        if (length < 4) {
            return 0;
        }

        payload = (uint64_t)data[2] << 8 | data[3];
        position = 4;

        if (payload < 126) {
            return WS_ERROR_PROTOCOL;
        }
    }
    else if (payload == 127) {
        if (length < 10) {
            return 0;
        }

        payload = 0;

        for (int index = 0; index < 8; index++) {
            payload = payload << 8 | data[2 + index];
        }

        position = 10;

        if (payload >> 63 || payload < 65536) {
            return WS_ERROR_PROTOCOL;
        }
    }

    if (opcode >= WS_CLOSE && (!fin || payload > 125)) {
        return WS_ERROR_PROTOCOL;
    }

    if (payload > max_payload) {
        return WS_ERROR_TOO_LARGE;
    }

    if (length < position + 4 || length - position - 4 < payload) {
        return 0;
    }

    unsigned char mask[4];

    memcpy(mask, data + position, 4);

    position += 4;

    for (uint64_t index = 0; index < payload; index++) {
        data[position + index] ^= mask[index & 3];
    }

    frame->fin = fin;
    frame->opcode = opcode;
    frame->length = payload;
    frame->payload = data + position;

    return (long)(position + payload);
}

size_t ws_frame_header(unsigned char *out, int opcode, uint64_t length) {
    out[0] = (unsigned char)(0x80 | (opcode & 0x0F));

    if (length < 126) {
        out[1] = (unsigned char)length;

        return 2;
    }

    if (length < 65536) {
        out[1] = 126;
        out[2] = (unsigned char)(length >> 8);
        out[3] = (unsigned char)length;

        return 4;
    }

    out[1] = 127;

    for (int index = 0; index < 8; index++) {
        out[2 + index] = (unsigned char)(length >> (56 - 8 * index));
    }

    return 10;
}

static int list_contains(const char *value, const char *token, int ignore_case) {
    size_t token_length = strlen(token);
    const char *cursor = value;

    while (*cursor) {
        while (*cursor == ' ' || *cursor == '\t' || *cursor == ',') {
            cursor++;
        }

        const char *start = cursor;

        while (*cursor && *cursor != ',') {
            cursor++;
        }

        const char *end = cursor;

        while (end > start && (end[-1] == ' ' || end[-1] == '\t')) {
            end--;
        }

        if ((size_t)(end - start) == token_length &&
            (ignore_case ? strncasecmp(start, token, token_length) : strncmp(start, token, token_length)) == 0) {
            return 1;
        }
    }

    return 0;
}

int ws_offers_protocol(const char *value, const char *protocol) {
    return list_contains(value, protocol, 0);
}

int ws_has_token(const char *value, const char *token) {
    return list_contains(value, token, 1);
}
