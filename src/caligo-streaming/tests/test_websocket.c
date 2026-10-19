#include "check.h"
#include "websocket.h"

static void hex(const unsigned char *bytes, size_t length, char *out) {
    static const char digits[] = "0123456789abcdef";

    for (size_t index = 0; index < length; index++) {
        out[index * 2] = digits[bytes[index] >> 4];
        out[index * 2 + 1] = digits[bytes[index] & 0x0F];
    }

    out[length * 2] = '\0';
}

static void sha1_matches_the_standard(void) {
    unsigned char digest[20];
    char text[41];
    const char *two_blocks = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";

    sha1((const unsigned char *)"abc", 3, digest);
    hex(digest, 20, text);
    CHECK_TEXT("a9993e364706816aba3e25717850c26c9cd0d89d", text);

    sha1((const unsigned char *)"", 0, digest);
    hex(digest, 20, text);
    CHECK_TEXT("da39a3ee5e6b4b0d3255bfef95601890afd80709", text);

    sha1((const unsigned char *)two_blocks, strlen(two_blocks), digest);
    hex(digest, 20, text);
    CHECK_TEXT("84983e441c3bd26ebaae4aa1f95129e5e54670f1", text);
}

static void base64_pads_like_rfc_4648(void) {
    char out[16];

    CHECK_EQUAL(0, base64_encode((const unsigned char *)"", 0, out, sizeof(out)));
    CHECK_TEXT("", out);
    base64_encode((const unsigned char *)"f", 1, out, sizeof(out));
    CHECK_TEXT("Zg==", out);
    base64_encode((const unsigned char *)"fo", 2, out, sizeof(out));
    CHECK_TEXT("Zm8=", out);
    base64_encode((const unsigned char *)"foo", 3, out, sizeof(out));
    CHECK_TEXT("Zm9v", out);
    CHECK_EQUAL(8, base64_encode((const unsigned char *)"foobar", 6, out, sizeof(out)));
    CHECK_TEXT("Zm9vYmFy", out);
    CHECK_EQUAL(0, base64_encode((const unsigned char *)"foobar", 6, out, 8));
}

static void the_accept_key_follows_rfc_6455(void) {
    char out[32];

    CHECK(ws_accept_key("dGhlIHNhbXBsZSBub25jZQ==", out, sizeof(out)));
    CHECK_TEXT("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", out);
    CHECK(!ws_accept_key("dGhlIHNhbXBsZSBub25jZQ", out, sizeof(out)));
    CHECK(!ws_accept_key("dGhlIHNhbXBsZSBub25jZQ=x", out, sizeof(out)));
    CHECK(!ws_accept_key("dGhl IHNhbXBsZSBub25jZQ==", out, sizeof(out)));
    CHECK(!ws_accept_key("dGhlIHNhbXBsZSBub25jZQ==", out, 20));
}

static void client_frames_are_unmasked(void) {
    unsigned char hello[] = { 0x81, 0x85, 0x37, 0xfa, 0x21, 0x3d, 0x7f, 0x9f, 0x4d, 0x51, 0x58 };
    unsigned char ping[] = { 0x89, 0x85, 0x37, 0xfa, 0x21, 0x3d, 0x7f, 0x9f, 0x4d, 0x51, 0x58 };
    WsFrame frame;

    CHECK_EQUAL(0, ws_parse_frame(hello, 1, 4096, &frame));
    CHECK_EQUAL(0, ws_parse_frame(hello, 10, 4096, &frame));
    CHECK_EQUAL(11, ws_parse_frame(hello, sizeof(hello), 4096, &frame));
    CHECK_EQUAL(1, frame.fin);
    CHECK_EQUAL(WS_TEXT, frame.opcode);
    CHECK_EQUAL(5, frame.length);
    CHECK(memcmp(frame.payload, "Hello", 5) == 0);

    CHECK_EQUAL(11, ws_parse_frame(ping, sizeof(ping), 4096, &frame));
    CHECK_EQUAL(WS_PING, frame.opcode);
    CHECK(memcmp(frame.payload, "Hello", 5) == 0);
}

static void longer_frames_use_extended_lengths(void) {
    static unsigned char data[8 + 200];
    WsFrame frame;

    data[0] = 0x82;
    data[1] = 0x80 | 126;
    data[2] = 0;
    data[3] = 200;
    data[4] = 1;
    data[5] = 2;
    data[6] = 3;
    data[7] = 4;

    for (int index = 0; index < 200; index++) {
        data[8 + index] = (unsigned char)(index ^ (index % 4 + 1));
    }

    CHECK_EQUAL(208, ws_parse_frame(data, sizeof(data), 4096, &frame));
    CHECK_EQUAL(200, frame.length);
    CHECK_EQUAL(WS_BINARY, frame.opcode);
    CHECK_EQUAL(199, frame.payload[199]);
    CHECK_EQUAL(0, frame.payload[0]);

    data[3] = 100;
    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(data, sizeof(data), 4096, &frame));

    data[3] = 200;
    CHECK_EQUAL(WS_ERROR_TOO_LARGE, ws_parse_frame(data, sizeof(data), 100, &frame));
}

static void broken_frames_are_refused(void) {
    unsigned char unmasked[] = { 0x82, 0x01, 0x00 };
    unsigned char reserved[] = { 0xC2, 0x81, 0, 0, 0, 0, 0 };
    unsigned char opcode[] = { 0x83, 0x81, 0, 0, 0, 0, 0 };
    unsigned char split_ping[] = { 0x09, 0x81, 0, 0, 0, 0, 0 };
    unsigned char long_ping[] = { 0x89, 0x80 | 126, 0, 126 };
    WsFrame frame;

    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(unmasked, sizeof(unmasked), 4096, &frame));
    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(reserved, sizeof(reserved), 4096, &frame));
    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(opcode, sizeof(opcode), 4096, &frame));
    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(split_ping, sizeof(split_ping), 4096, &frame));
    CHECK_EQUAL(WS_ERROR_PROTOCOL, ws_parse_frame(long_ping, sizeof(long_ping), 4096, &frame));
}

static void server_headers_pick_the_shortest_length(void) {
    unsigned char out[10];

    CHECK_EQUAL(2, ws_frame_header(out, WS_BINARY, 5));
    CHECK_EQUAL(0x82, out[0]);
    CHECK_EQUAL(5, out[1]);
    CHECK_EQUAL(4, ws_frame_header(out, WS_BINARY, 200));
    CHECK_EQUAL(126, out[1]);
    CHECK_EQUAL(0, out[2]);
    CHECK_EQUAL(200, out[3]);
    CHECK_EQUAL(10, ws_frame_header(out, WS_PING, 70000));
    CHECK_EQUAL(0x89, out[0]);
    CHECK_EQUAL(127, out[1]);
    CHECK_EQUAL(0x01, out[7]);
    CHECK_EQUAL(0x11, out[8]);
    CHECK_EQUAL(0x70, out[9]);
}

static void header_lists_are_matched_by_token(void) {
    CHECK(ws_offers_protocol("moq-lite-07, caligo-objects-0", "caligo-objects-0"));
    CHECK(ws_offers_protocol("caligo-objects-0", "caligo-objects-0"));
    CHECK(!ws_offers_protocol("caligo-objects-00", "caligo-objects-0"));
    CHECK(!ws_offers_protocol("CALIGO-OBJECTS-0", "caligo-objects-0"));
    CHECK(!ws_offers_protocol("", "caligo-objects-0"));
    CHECK(ws_has_token("keep-alive, Upgrade", "upgrade"));
    CHECK(ws_has_token("WebSocket", "websocket"));
    CHECK(!ws_has_token("keep-alive", "upgrade"));
}

int main(void) {
    sha1_matches_the_standard();
    base64_pads_like_rfc_4648();
    the_accept_key_follows_rfc_6455();
    client_frames_are_unmasked();
    longer_frames_use_extended_lengths();
    broken_frames_are_refused();
    server_headers_pick_the_shortest_length();
    header_lists_are_matched_by_token();

    CHECK_REPORT("websocket");
}
