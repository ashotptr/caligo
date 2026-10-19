#ifndef RELAY_WEBSOCKET_H
#define RELAY_WEBSOCKET_H

#include <stddef.h>
#include <stdint.h>

#define WS_CONTINUATION 0x0
#define WS_TEXT 0x1
#define WS_BINARY 0x2
#define WS_CLOSE 0x8
#define WS_PING 0x9
#define WS_PONG 0xA

#define WS_ERROR_PROTOCOL -1
#define WS_ERROR_TOO_LARGE -2

typedef struct {
    int fin;
    int opcode;
    uint64_t length;
    unsigned char *payload;
} WsFrame;

void sha1(const unsigned char *data, size_t length, unsigned char digest[20]);

size_t base64_encode(const unsigned char *data, size_t length, char *out, size_t capacity);

int ws_accept_key(const char *key, char *out, size_t capacity);

long ws_parse_frame(unsigned char *data, size_t length, uint64_t max_payload, WsFrame *frame);

size_t ws_frame_header(unsigned char *out, int opcode, uint64_t length);

int ws_offers_protocol(const char *value, const char *protocol);

int ws_has_token(const char *value, const char *token);

#endif
