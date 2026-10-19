#include "check.h"
#include "http.h"

#include <stdlib.h>

static unsigned char decoded[65536];
static size_t decoded_length;

static void collect(void *context, const unsigned char *data, size_t length) {
    (void)context;

    if (decoded_length + length <= sizeof(decoded)) {
        memcpy(decoded + decoded_length, data, length);

        decoded_length += length;
    }
}

static int parse(const char *text, HttpRequest *request) {
    return http_parse_request(text, strlen(text), request);
}

static void a_browser_request_is_understood(void) {
    HttpRequest request;
    const char *text =
        "GET /live/?t=1791925326375 HTTP/1.1\r\n"
        "Host: caligo.example\r\n"
        "range: bytes=196608-\r\n"
        "Accept: */*\r\n\r\n";

    CHECK(http_request_complete(text, strlen(text)));
    CHECK_EQUAL(0, parse(text, &request));
    CHECK_TEXT("GET", request.method);
    CHECK_TEXT("/live/?t=1791925326375", request.target);
    CHECK_TEXT("/live/", request.path);
    CHECK_EQUAL(1, request.has_query);
    CHECK_EQUAL(1, request.version_minor);
    CHECK_EQUAL(1, request.has_range);
    CHECK_EQUAL(196608, request.range_start);
    CHECK_EQUAL(0, request.wants_icy);
    CHECK_EQUAL(0, request.wants_events);
}

static void other_request_shapes(void) {
    HttpRequest request;

    CHECK_EQUAL(0, parse("HEAD /live/stream HTTP/1.0\r\nIcy-MetaData: 1\r\n\r\n", &request));
    CHECK_TEXT("HEAD", request.method);
    CHECK_TEXT("/live/stream", request.path);
    CHECK_EQUAL(0, request.has_query);
    CHECK_EQUAL(0, request.version_minor);
    CHECK_EQUAL(0, request.has_range);
    CHECK_EQUAL(1, request.wants_icy);

    CHECK_EQUAL(0, parse("GET /live/events HTTP/1.1\r\nAccept: text/event-stream\r\n\r\n", &request));
    CHECK_EQUAL(1, request.wants_events);

    CHECK_EQUAL(0, parse("GET /live/? HTTP/1.1\r\nRange: bytes=0-\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_query);
    CHECK_EQUAL(1, request.has_range);
    CHECK_EQUAL(0, request.range_start);

    CHECK_EQUAL(0, parse("GET / HTTP/1.1\r\nRange: bytes=100-200\r\n\r\n", &request));
    CHECK_EQUAL(100, request.range_start);
}

static void ranges_that_are_not_resumes_are_ignored(void) {
    HttpRequest request;

    CHECK_EQUAL(0, parse("GET / HTTP/1.1\r\nRange: bytes=-500\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_range);

    CHECK_EQUAL(0, parse("GET / HTTP/1.1\r\nRange: items=5-\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_range);

    CHECK_EQUAL(0, parse("GET / HTTP/1.1\r\nRange: bytes=99999999999999999999999-\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_range);

    CHECK_EQUAL(0, parse("GET / HTTP/1.1\r\nX-Range: bytes=5-\r\nContent-Range: bytes=7-\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_range);

    CHECK_EQUAL(0, parse("GET /orange.mp3 HTTP/1.1\r\nHost: range\r\n\r\n", &request));
    CHECK_EQUAL(0, request.has_range);
}

static void broken_requests_are_refused(void) {
    HttpRequest request;
    char long_target[3000];

    CHECK_EQUAL(-1, parse("GET\r\n\r\n", &request));
    CHECK_EQUAL(-1, parse("GET /live/\r\n\r\n", &request));
    CHECK_EQUAL(-1, parse("GET live HTTP/1.1\r\n\r\n", &request));
    CHECK_EQUAL(-1, parse("GET /live/ HTTP/2.0\r\n\r\n", &request));
    CHECK_EQUAL(-1, parse("TOOLONGMETHOD / HTTP/1.1\r\n\r\n", &request));
    CHECK_EQUAL(-1, parse(" / HTTP/1.1\r\n\r\n", &request));

    memset(long_target, 'a', sizeof(long_target));
    memcpy(long_target, "GET /", 5);
    memcpy(long_target + sizeof(long_target) - 16, " HTTP/1.1\r\n\r\n", 14);

    CHECK_EQUAL(-1, parse(long_target, &request));
    CHECK(!http_request_complete("GET / HTTP/1.1\r\nHost: x\r\n", 25));
}

static void headers_are_found_by_exact_name(void) {
    char value[32];
    const char *headers = "Host: example\r\nTransfer-Encoding:  chunked \r\nX-Long: 0123456789012345678901234567890123456789\r\n";

    CHECK(http_header_value(headers, "transfer-encoding", value, sizeof(value)));
    CHECK_TEXT("chunked", value);
    CHECK(http_header_value(headers, "X-Long", value, sizeof(value)));
    CHECK_EQUAL(31, strlen(value));
    CHECK(!http_header_value(headers, "Encoding", value, sizeof(value)));
    CHECK(!http_header_value("", "Host", value, sizeof(value)));
}

static void chunks_are_decoded_however_they_arrive(void) {
    const char *wire = "5\r\nhello\r\n1A;note=1\r\nabcdefghijklmnopqrstuvwxyz\r\nb\r\n world, bye\r\n0\r\n\r\n";
    const char *plain = "helloabcdefghijklmnopqrstuvwxyz world, bye";
    ChunkDecoder decoder;

    for (size_t piece = 1; piece <= strlen(wire); piece++) {
        int result = 0;

        decoded_length = 0;

        chunk_decoder_init(&decoder);

        for (size_t offset = 0; offset < strlen(wire) && result == 0; offset += piece) {
            size_t take = strlen(wire) - offset < piece ? strlen(wire) - offset : piece;

            result = chunk_decoder_feed(&decoder, (const unsigned char *)wire + offset, take, collect, NULL);
        }

        CHECK_EQUAL(1, result);
        CHECK(decoded_length == strlen(plain) && memcmp(decoded, plain, decoded_length) == 0);
    }
}

static void broken_chunks_are_an_error(void) {
    ChunkDecoder decoder;

    chunk_decoder_init(&decoder);
    CHECK_EQUAL(-1, chunk_decoder_feed(&decoder, (const unsigned char *)"zz\r\n", 4, collect, NULL));

    chunk_decoder_init(&decoder);
    CHECK_EQUAL(-1, chunk_decoder_feed(&decoder, (const unsigned char *)"3\r\nabcXX", 8, collect, NULL));

    chunk_decoder_init(&decoder);
    CHECK_EQUAL(-1, chunk_decoder_feed(&decoder, (const unsigned char *)"\r\n", 2, collect, NULL));

    chunk_decoder_init(&decoder);
    CHECK_EQUAL(-1, chunk_decoder_feed(&decoder, (const unsigned char *)"11111111111111111\r\n", 19, collect, NULL));
}

static void caligos_event_is_read(void) {
    RadioEvent event;

    CHECK(radio_event_parse(
        " {\"playing\": \"public_html/music/a.mp3\", \"artist\": \"Radiohead\", \"album\": \"OK Computer\", "
        "\"title\": \"Lucky\", \"bitrate\": 128, \"is_request\": false}", &event));
    CHECK_TEXT("public_html/music/a.mp3", event.playing);
    CHECK_TEXT("Radiohead", event.artist);
    CHECK_TEXT("OK Computer", event.album);
    CHECK_TEXT("Lucky", event.title);
    CHECK_EQUAL(0, event.is_request);

    CHECK(radio_event_parse(
        "{\"playing\": \"/tmp/radio_1.mp3\", \"artist\": \"Arctic Monkeys\", \"album\": \"AM\", "
        "\"title\": \"Why'd You Only Call Me \"When\" You're High?\", \"bitrate\": 128, \"is_request\": true}", &event));
    CHECK_TEXT("Why'd You Only Call Me \"When\" You're High?", event.title);
    CHECK_TEXT("Arctic Monkeys", event.artist);
    CHECK_EQUAL(1, event.is_request);

    CHECK(!radio_event_parse("{\"title\": \"No Other Fields\"}", &event));
    CHECK(!radio_event_parse("", &event));
    CHECK(!radio_event_parse(
        "{\"playing\": \"a\", \"artist\": \"b\", \"album\": \"c\", \"title\": \"\", \"bitrate\": 128, \"is_request\": false}", &event));
}

static void json_text_is_escaped(void) {
    char target[64];
    char small[8];

    json_escape(target, sizeof(target), "He said \"hi\" \\ bye\n");
    CHECK_TEXT("He said \\\"hi\\\" \\\\ bye\\u000a", target);

    json_escape(target, sizeof(target), "caf\xC3\xA9");
    CHECK_TEXT("caf\xC3\xA9", target);

    json_escape(small, sizeof(small), "abcdefghijkl");
    CHECK_TEXT("abcdefg", small);

    json_escape(small, sizeof(small), "abcdef\"");
    CHECK_TEXT("abcdef", small);
}

static void hashes_tell_targets_apart(void) {
    CHECK(hash_text("/live/?t=1") != hash_text("/live/?t=2"));
    CHECK(hash_text("/live/?t=1") == hash_text("/live/?t=1"));
    CHECK(hash_text("") != 0);
}

static void byte_ranges_follow_rfc_9110(void) {
    uint64_t start = 0;
    uint64_t length = 0;

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("bytes=0-99", 1000, &start, &length));
    CHECK_EQUAL(0, start);
    CHECK_EQUAL(100, length);

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("bytes=500-", 1000, &start, &length));
    CHECK_EQUAL(500, start);
    CHECK_EQUAL(500, length);

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("bytes=900-5000", 1000, &start, &length));
    CHECK_EQUAL(900, start);
    CHECK_EQUAL(100, length);

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("bytes=-200", 1000, &start, &length));
    CHECK_EQUAL(800, start);
    CHECK_EQUAL(200, length);

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("bytes=-5000", 1000, &start, &length));
    CHECK_EQUAL(0, start);
    CHECK_EQUAL(1000, length);

    CHECK_EQUAL(RANGE_PARTIAL, http_byte_range("Bytes=999-999", 1000, &start, &length));
    CHECK_EQUAL(999, start);
    CHECK_EQUAL(1, length);

    CHECK_EQUAL(RANGE_UNSATISFIABLE, http_byte_range("bytes=1000-", 1000, &start, &length));
    CHECK_EQUAL(RANGE_UNSATISFIABLE, http_byte_range("bytes=1000-2000", 1000, &start, &length));
    CHECK_EQUAL(RANGE_UNSATISFIABLE, http_byte_range("bytes=-0", 1000, &start, &length));
    CHECK_EQUAL(RANGE_UNSATISFIABLE, http_byte_range("bytes=0-", 0, &start, &length));

    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=5-1", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=0-1,5-9", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("items=0-1", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=-", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=a-b", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=0-9x", 1000, &start, &length));
    CHECK_EQUAL(RANGE_NONE, http_byte_range("bytes=99999999999999999999-", 1000, &start, &length));
}

static void source_credentials_are_decoded(void) {
    char user[64];
    char password[64];

    CHECK(http_basic_credentials("Basic c291cmNlOmhhY2ttZQ==", user, sizeof(user), password, sizeof(password)));
    CHECK_TEXT("source", user);
    CHECK_TEXT("hackme", password);
    CHECK(http_basic_credentials("basic   c291cmNlOmE6Yg", user, sizeof(user), password, sizeof(password)));
    CHECK_TEXT("source", user);
    CHECK_TEXT("a:b", password);
    CHECK(!http_basic_credentials("Bearer c291cmNlOmhhY2ttZQ==", user, sizeof(user), password, sizeof(password)));
    CHECK(!http_basic_credentials("Basic c291cmNl", user, sizeof(user), password, sizeof(password)));
    CHECK(!http_basic_credentials("Basic ****", user, sizeof(user), password, sizeof(password)));
    CHECK(!http_basic_credentials("Basic c291cmNlOmhhY2ttZQ==x", user, sizeof(user), password, sizeof(password)));
    CHECK(!http_basic_credentials("Basic c291cmNlOmhhY2ttZQ==", user, 4, password, sizeof(password)));
    CHECK(!http_basic_credentials("Basic c291cmNlOmhhY2ttZQ==", user, sizeof(user), password, 6));
    CHECK(!http_basic_credentials("Basic AA==", user, sizeof(user), password, sizeof(password)));
}

static void query_values_are_decoded(void) {
    char value[64];

    CHECK(http_query_value(
        "/admin/metadata?mount=%2Flive%2F&mode=updinfo&song=Arctic+Monkeys+-+R+U+Mine%3F", "song", value, sizeof(value)));
    CHECK_TEXT("Arctic Monkeys - R U Mine?", value);
    CHECK(http_query_value("/admin/metadata?mount=%2Flive%2F&mode=updinfo", "mount", value, sizeof(value)));
    CHECK_TEXT("/live/", value);
    CHECK(http_query_value("/x?flag&mode=", "flag", value, sizeof(value)));
    CHECK_TEXT("", value);
    CHECK(http_query_value("/x?flag&mode=", "mode", value, sizeof(value)));
    CHECK_TEXT("", value);
    CHECK(!http_query_value("/x?modes=1", "mode", value, sizeof(value)));
    CHECK(!http_query_value("/x", "mode", value, sizeof(value)));
    CHECK(http_query_value("/x?a=%zz%4", "a", value, sizeof(value)));
    CHECK_TEXT("%zz%4", value);
    CHECK(http_query_value("/x?a=abcdefgh", "a", value, 4));
    CHECK_TEXT("abc", value);
}

static void metadata_text_becomes_clean_utf8(void) {
    char out[32];

    text_utf8_clean("Eres Tú", out, sizeof(out));
    CHECK_TEXT("Eres Tú", out);

    text_utf8_clean("Caf\xE9", out, sizeof(out));
    CHECK_TEXT("Caf\xC3\xA9", out);

    text_utf8_clean("a\tb\nc\x7F", out, sizeof(out));
    CHECK_TEXT("a b c ", out);

    text_utf8_clean("ééééé", out, 6);
    CHECK_TEXT("éé", out);

    text_utf8_clean("\xED\xA0\x80", out, sizeof(out));
    CHECK_EQUAL(6, strlen(out));
}

static void secrets_compare_whole(void) {
    CHECK(text_equal_secret("hackme", "hackme"));
    CHECK(!text_equal_secret("hackm", "hackme"));
    CHECK(!text_equal_secret("hackmee", "hackme"));
    CHECK(!text_equal_secret("hackmf", "hackme"));
    CHECK(!text_equal_secret("", ""));
}

int main(void) {
    a_browser_request_is_understood();
    other_request_shapes();
    ranges_that_are_not_resumes_are_ignored();
    broken_requests_are_refused();
    headers_are_found_by_exact_name();
    chunks_are_decoded_however_they_arrive();
    broken_chunks_are_an_error();
    caligos_event_is_read();
    json_text_is_escaped();
    hashes_tell_targets_apart();
    byte_ranges_follow_rfc_9110();
    source_credentials_are_decoded();
    query_values_are_decoded();
    metadata_text_becomes_clean_utf8();
    secrets_compare_whole();

    CHECK_REPORT("http");
}
