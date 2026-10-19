#define _GNU_SOURCE

#include "client.h"
#include "http.h"

#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define CLIENT_RAW_MAX (CLIENT_BODY_MAX + 16384)

typedef struct {
    ClientResponse *response;
    int overflow;
} Collector;

static long long monotonic_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (long long)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int remaining(long long deadline) {
    long long left = deadline - monotonic_ms();

    return left > 0 ? (int)left : 0;
}

static int connect_to(const char *host, int port, long long deadline) {
    char service[16];
    struct addrinfo hints;
    struct addrinfo *found = NULL;

    snprintf(service, sizeof(service), "%d", port);
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;

    if (getaddrinfo(host, service, &hints, &found) != 0) {
        return -1;
    }

    int fd = -1;

    for (struct addrinfo *candidate = found; candidate && fd < 0; candidate = candidate->ai_next) {
        fd = socket(candidate->ai_family, candidate->ai_socktype | SOCK_NONBLOCK | SOCK_CLOEXEC, candidate->ai_protocol);

        if (fd < 0) {
            continue;
        }

        if (connect(fd, candidate->ai_addr, candidate->ai_addrlen) == 0) {
            break;
        }

        if (errno == EINPROGRESS) {
            struct pollfd wait = { .fd = fd, .events = POLLOUT, .revents = 0 };
            int error = 0;
            socklen_t error_length = sizeof(error);

            if (poll(&wait, 1, remaining(deadline)) == 1 &&
                getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &error_length) == 0 && error == 0) {
                break;
            }
        }

        close(fd);
        fd = -1;
    }

    freeaddrinfo(found);

    return fd;
}

static int send_all(int fd, const char *data, size_t length, long long deadline) {
    size_t sent = 0;

    while (sent < length) {
        ssize_t wrote = send(fd, data + sent, length - sent, MSG_NOSIGNAL);

        if (wrote > 0) {
            sent += (size_t)wrote;

            continue;
        }

        if (wrote < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
            return -1;
        }

        struct pollfd wait = { .fd = fd, .events = POLLOUT, .revents = 0 };

        if (poll(&wait, 1, remaining(deadline)) != 1) {
            return -1;
        }
    }

    return 0;
}

static void collect(void *context, const unsigned char *data, size_t length) {
    Collector *collector = context;
    ClientResponse *response = collector->response;

    if (response->body_length + length > CLIENT_BODY_MAX) {
        collector->overflow = 1;

        return;
    }

    memcpy(response->body + response->body_length, data, length);

    response->body_length += length;
}

int client_parse_response(const char *raw, size_t length, ClientResponse *response) {
    response->status = 0;
    response->body_length = 0;
    response->body[0] = '\0';

    const char *end = memmem(raw, length, "\r\n\r\n", 4);

    if (!end || length < 12 || memcmp(raw, "HTTP/1.", 7) != 0 || raw[8] != ' ') {
        return -1;
    }

    int status = 0;

    for (int i = 9; i < 12; i++) {
        if (raw[i] < '0' || raw[i] > '9') {
            return -1;
        }

        status = status * 10 + (raw[i] - '0');
    }

    char headers[8192];
    size_t header_length = (size_t)(end - raw) + 2;

    if (header_length >= sizeof(headers)) {
        return -1;
    }

    memcpy(headers, raw, header_length);
    headers[header_length] = '\0';

    const char *first_header = strstr(headers, "\r\n");
    const char *body = end + 4;
    size_t body_length = length - (size_t)(body - raw);
    char value[64];

    response->status = status;

    if (first_header && http_header_value(first_header + 2, "Transfer-Encoding", value, sizeof(value)) && strcasecmp(value, "chunked") == 0) {
        ChunkDecoder decoder;
        Collector collector = { .response = response, .overflow = 0 };

        chunk_decoder_init(&decoder);

        if (chunk_decoder_feed(&decoder, (const unsigned char *)body, body_length, collect, &collector) < 0 || collector.overflow) {
            return -1;
        }
    } else {
        if (body_length > CLIENT_BODY_MAX) {
            return -1;
        }

        memcpy(response->body, body, body_length);

        response->body_length = body_length;
    }

    response->body[response->body_length] = '\0';

    return 0;
}

int client_call(const char *host, int port, const char *method, const char *path,
                const char *extra_headers, const char *body, size_t body_length,
                int timeout_ms, ClientResponse *response) {
    long long deadline = monotonic_ms() + timeout_ms;
    int fd = connect_to(host, port, deadline);

    response->status = 0;
    response->body_length = 0;
    response->body[0] = '\0';

    if (fd < 0) {
        return -1;
    }

    char head[2048];
    int head_length = snprintf(head, sizeof(head),
                               "%s %s HTTP/1.0\r\nHost: %s:%d\r\nConnection: close\r\nAccept: application/json\r\n"
                               "Content-Type: application/json\r\nContent-Length: %zu\r\n%s\r\n",
                               method, path, host, port, body_length, extra_headers ? extra_headers : "");

    if (head_length < 0 || (size_t)head_length >= sizeof(head) ||
        send_all(fd, head, (size_t)head_length, deadline) != 0 ||
        (body_length > 0 && send_all(fd, body, body_length, deadline) != 0)) {
        close(fd);

        return -1;
    }

    char *raw = malloc(CLIENT_RAW_MAX);
    size_t raw_length = 0;
    int failed = raw == NULL;

    while (!failed) {
        ssize_t got = recv(fd, raw + raw_length, CLIENT_RAW_MAX - raw_length, 0);

        if (got > 0) {
            raw_length += (size_t)got;

            if (raw_length == CLIENT_RAW_MAX) {
                failed = 1;
            }

            continue;
        }

        if (got == 0) {
            break;
        }

        if (errno == EINTR) {
            continue;
        }

        if (errno != EAGAIN && errno != EWOULDBLOCK) {
            failed = 1;

            break;
        }

        struct pollfd wait = { .fd = fd, .events = POLLIN, .revents = 0 };

        if (poll(&wait, 1, remaining(deadline)) != 1) {
            failed = 1;
        }
    }

    close(fd);

    int result = failed ? -1 : client_parse_response(raw, raw_length, response);

    free(raw);

    return result;
}
