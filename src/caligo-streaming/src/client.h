#ifndef CALIGO_CLIENT_H
#define CALIGO_CLIENT_H

#include <stddef.h>

#define CLIENT_BODY_MAX 65536

typedef struct {
    int status;
    char body[CLIENT_BODY_MAX + 1];
    size_t body_length;
} ClientResponse;

int client_call(const char *host, int port, const char *method, const char *path,
                const char *extra_headers, const char *body, size_t body_length,
                int timeout_ms, ClientResponse *response);

int client_parse_response(const char *raw, size_t length, ClientResponse *response);

#endif
