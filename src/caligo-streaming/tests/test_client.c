#include "check.h"
#include "client.h"

#include <stdlib.h>

static ClientResponse response;

static void a_plain_response_is_read(void) {
    static const char raw[] =
        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 27\r\n\r\n"
        "{\"live_until\": 1760443200}\n";

    CHECK_EQUAL(0, client_parse_response(raw, sizeof(raw) - 1, &response));
    CHECK_EQUAL(200, response.status);
    CHECK_EQUAL(27, response.body_length);
    CHECK_TEXT("{\"live_until\": 1760443200}\n", response.body);
}

static void a_chunked_response_is_joined(void) {
    static const char raw[] =
        "HTTP/1.1 200 OK\r\ntransfer-encoding: chunked\r\n\r\n"
        "5\r\nhello\r\n7\r\n, world\r\n0\r\n\r\n";

    CHECK_EQUAL(0, client_parse_response(raw, sizeof(raw) - 1, &response));
    CHECK_EQUAL(200, response.status);
    CHECK_TEXT("hello, world", response.body);
}

static void statuses_without_a_body_are_read(void) {
    static const char gone[] = "HTTP/1.1 410 Gone\r\nContent-Length: 0\r\n\r\n";
    static const char empty[] = "HTTP/1.0 204 No Content\r\n\r\n";

    CHECK_EQUAL(0, client_parse_response(gone, sizeof(gone) - 1, &response));
    CHECK_EQUAL(410, response.status);
    CHECK_EQUAL(0, response.body_length);
    CHECK_EQUAL(0, client_parse_response(empty, sizeof(empty) - 1, &response));
    CHECK_EQUAL(204, response.status);
}

static void broken_responses_are_refused(void) {
    static const char unfinished[] = "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n";
    static const char not_http[] = "SSH-2.0-OpenSSH\r\n\r\n";
    static const char bad_status[] = "HTTP/1.1 2x0 OK\r\n\r\n";
    static const char bad_chunks[] = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n";

    CHECK_EQUAL(-1, client_parse_response(unfinished, sizeof(unfinished) - 1, &response));
    CHECK_EQUAL(-1, client_parse_response(not_http, sizeof(not_http) - 1, &response));
    CHECK_EQUAL(-1, client_parse_response(bad_status, sizeof(bad_status) - 1, &response));
    CHECK_EQUAL(-1, client_parse_response(bad_chunks, sizeof(bad_chunks) - 1, &response));
}

static void nobody_answering_is_an_error(void) {
    CHECK_EQUAL(-1, client_call("127.0.0.1", 1, "GET", "/", NULL, NULL, 0, 500, &response));
    CHECK_EQUAL(-1, client_call("name.that.does.not.resolve.invalid", 80, "GET", "/", NULL, NULL, 0, 500, &response));
}

int main(void) {
    a_plain_response_is_read();
    a_chunked_response_is_joined();
    statuses_without_a_body_are_read();
    broken_responses_are_refused();
    nobody_answering_is_an_error();

    CHECK_REPORT("client");
}
