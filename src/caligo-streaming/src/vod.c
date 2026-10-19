#define _GNU_SOURCE

#include "client.h"
#include "http.h"
#include "media.h"

#include <errno.h>
#include <fcntl.h>
#include <jansson.h>
#include <linux/openat2.h>
#include <netdb.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/sendfile.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#define KIND_LISTEN 1
#define KIND_LOOKUP 2
#define KIND_CLIENT 3

#define REQUEST_MAX 8192
#define HEAD_MAX 2048
#define CACHE_SLOTS 4096
#define CACHE_PROBES 8
#define QUEUE_MAX 1024
#define SEND_CHUNK (1024 * 1024)
#define MAX_LOOKUP_THREADS 16
#define ERROR_CACHE_MS 500
#define DRAIN_MS 2000

typedef enum { LOOKUP_LIVE, LOOKUP_GONE, LOOKUP_UNKNOWN, LOOKUP_ERROR } LookupOutcome;

typedef enum { ENTRY_EMPTY, ENTRY_PENDING, ENTRY_READY } EntryState;

typedef struct {
    unsigned char session[MEDIA_SESSION_BYTES];
    EntryState state;
    LookupOutcome outcome;
    int64_t live_until;
    char storage[MEDIA_PATH_MAX + 1];
    int64_t fetched_ms;
    int64_t used_ms;
} Entry;

typedef enum { CLIENT_READ, CLIENT_WAIT, CLIENT_SEND, CLIENT_DRAIN } ClientState;

typedef struct {
    int kind;
    int fd;
    int slot;
    ClientState state;
    char in[REQUEST_MAX];
    size_t in_length;
    size_t request_length;
    char head[HEAD_MAX];
    size_t head_length;
    size_t head_sent;
    int file_fd;
    off_t file_offset;
    uint64_t file_left;
    int keep_alive;
    int head_only;
    uint32_t interest;
    int entry;
    MediaGrant grant;
    char media[MEDIA_PATH_MAX + 1];
    char range[128];
    int has_range;
    int64_t activity_ms;
    int64_t waiting_since_ms;
    int64_t drain_since_ms;
} Client;

typedef struct {
    char listen_host[64];
    int listen_port;
    char api_host[256];
    int api_port;
    char api_path[256];
    char library[512];
    unsigned char key[512];
    size_t key_length;
    char internal_key[MEDIA_INTERNAL_KEY_HEX + 1];
    int cache_ms;
    int max_clients;
    int request_timeout;
    int idle_timeout;
    int write_timeout;
    int lookup_timeout_ms;
    int lookup_threads;
} Config;

typedef struct {
    int64_t started_ms;
    uint64_t accepted;
    uint64_t refused;
    uint64_t requests;
    uint64_t ok;
    uint64_t partial;
    uint64_t forbidden;
    uint64_t gone;
    uint64_t not_found;
    uint64_t unavailable;
    uint64_t unsatisfiable;
    uint64_t bad;
    uint64_t bytes_out;
    uint64_t lookups;
    uint64_t lookup_errors;
    uint64_t cache_hits;
    int clients;
    int peak_clients;
} Stats;

static Config config;
static Stats stats;
static Entry cache[CACHE_SLOTS];
static Client **clients;
static int epoll_fd = -1;
static int listen_fd = -1;
static int library_fd = -1;
static int lookup_fd = -1;
static int listen_kind = KIND_LISTEN;
static int lookup_kind = KIND_LOOKUP;
static volatile sig_atomic_t running = 1;

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t wake = PTHREAD_COND_INITIALIZER;
static int queue[QUEUE_MAX];
static int queue_head;
static int queue_count;
static int stopping;
static pthread_t threads[MAX_LOOKUP_THREADS];
static int64_t last_error_log_ms;

static void client_flush(Client *client);

static int64_t now_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int64_t wall_seconds(void) {
    return (int64_t)time(NULL);
}

static void say(const char *format, ...) {
    char text[512];
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);

    fprintf(stderr, "[vod] %s\n", text);
}

static const char *env_text(const char *name, const char *fallback) {
    const char *value = getenv(name);

    return value && *value ? value : fallback;
}

static long env_number(const char *name, long fallback, long minimum, long maximum) {
    const char *value = getenv(name);

    if (!value || !*value) {
        return fallback;
    }

    char *end = NULL;
    long number = strtol(value, &end, 10);

    if (end == value || *end != '\0' || number < minimum || number > maximum) {
        say("%s=%s is not a number between %ld and %ld, using %ld", name, value, minimum, maximum, fallback);

        return fallback;
    }

    return number;
}

static int load_config(void) {
    snprintf(config.listen_host, sizeof(config.listen_host), "%s", env_text("VOD_LISTEN_HOST", "0.0.0.0"));
    snprintf(config.api_host, sizeof(config.api_host), "%s", env_text("API_HOST", "caligo-api"));
    snprintf(config.api_path, sizeof(config.api_path), "%s", env_text("VOD_SESSIONS_PATH", "/internal/v1/media/sessions/"));
    snprintf(config.library, sizeof(config.library), "%s", env_text("VOD_LIBRARY", "/media/library"));

    config.listen_port = (int)env_number("VOD_LISTEN_PORT", 8010, 1, 65535);
    config.api_port = (int)env_number("API_PORT", 8080, 1, 65535);
    config.cache_ms = (int)env_number("VOD_CACHE_MS", 2000, 0, 60000);
    config.max_clients = (int)env_number("VOD_MAX_CLIENTS", 4096, 1, 100000);
    config.request_timeout = (int)env_number("VOD_REQUEST_TIMEOUT", 10, 1, 3600);
    config.idle_timeout = (int)env_number("VOD_IDLE_TIMEOUT", 30, 1, 3600);
    config.write_timeout = (int)env_number("VOD_WRITE_TIMEOUT", 60, 1, 3600);
    config.lookup_timeout_ms = (int)env_number("VOD_LOOKUP_TIMEOUT_MS", 3000, 100, 60000);
    config.lookup_threads = (int)env_number("VOD_LOOKUP_THREADS", 2, 1, MAX_LOOKUP_THREADS);

    const char *key = getenv("MEDIA_SIGNING_KEY");
    size_t key_length = key ? strlen(key) : 0;

    if (key_length < 32 || key_length > sizeof(config.key)) {
        say("MEDIA_SIGNING_KEY must be set to the same key as the API's Media:SigningKey (32 to %zu bytes)", sizeof(config.key));

        return -1;
    }

    memcpy(config.key, key, key_length);

    config.key_length = key_length;

    media_internal_key(config.key, config.key_length, config.internal_key);

    return config.internal_key[0] ? 0 : -1;
}

static void watch(int fd, uint32_t interest, void *tag, int operation) {
    struct epoll_event event;

    memset(&event, 0, sizeof(event));

    event.events = interest;
    event.data.ptr = tag;

    epoll_ctl(epoll_fd, operation, fd, &event);
}

static void client_interest(Client *client, uint32_t interest) {
    if (client->interest != interest) {
        client->interest = interest;

        watch(client->fd, interest, client, EPOLL_CTL_MOD);
    }
}

static uint32_t session_hash(const unsigned char session[MEDIA_SESSION_BYTES]) {
    uint32_t hash = 2166136261u;

    for (int i = 0; i < MEDIA_SESSION_BYTES; i++) {
        hash = (hash ^ session[i]) * 16777619u;
    }

    return hash;
}

static int entry_fresh(const Entry *entry, int64_t now) {
    if (entry->state != ENTRY_READY) {
        return 0;
    }

    int64_t age = now - entry->fetched_ms;

    if (entry->outcome == LOOKUP_ERROR) {
        return age < ERROR_CACHE_MS;
    }

    if (entry->outcome == LOOKUP_LIVE && entry->live_until <= wall_seconds()) {
        return 0;
    }

    return age < config.cache_ms;
}

static int cache_slot(const unsigned char session[MEDIA_SESSION_BYTES], int64_t now) {
    uint32_t base = session_hash(session);
    int victim = -1;

    for (int probe = 0; probe < CACHE_PROBES; probe++) {
        int slot = (int)((base + (uint32_t)probe) & (CACHE_SLOTS - 1));
        Entry *entry = &cache[slot];

        if (entry->state != ENTRY_EMPTY && memcmp(entry->session, session, MEDIA_SESSION_BYTES) == 0) {
            return slot;
        }

        if (entry->state == ENTRY_EMPTY) {
            if (victim < 0 || cache[victim].state != ENTRY_EMPTY) {
                victim = slot;
            }
        }
        else if (entry->state == ENTRY_READY && (victim < 0 ||
                 (cache[victim].state == ENTRY_READY && entry->used_ms < cache[victim].used_ms))) {
            victim = slot;
        }
    }

    if (victim < 0) {
        return -1;
    }

    Entry *entry = &cache[victim];

    memset(entry, 0, sizeof(*entry));
    memcpy(entry->session, session, MEDIA_SESSION_BYTES);

    entry->state = ENTRY_EMPTY;
    entry->used_ms = now;

    return victim;
}

static void fetch(const unsigned char session[MEDIA_SESSION_BYTES], Entry *result) {
    char uuid[MEDIA_UUID_TEXT + 1];
    char path[512];
    char headers[160];
    ClientResponse *response = malloc(sizeof(*response));

    result->outcome = LOOKUP_ERROR;
    result->live_until = 0;
    result->storage[0] = '\0';

    if (!response) {
        return;
    }

    media_uuid_text(session, uuid);
    snprintf(path, sizeof(path), "%s%s", config.api_path, uuid);
    snprintf(headers, sizeof(headers), "X-Caligo-Key: %s\r\n", config.internal_key);

    if (client_call(config.api_host, config.api_port, "GET", path, headers, NULL, 0, config.lookup_timeout_ms, response) != 0) {
        free(response);

        return;
    }

    if (response->status == 404) {
        result->outcome = LOOKUP_UNKNOWN;
    }
    else if (response->status == 410) {
        result->outcome = LOOKUP_GONE;
    }
    else if (response->status == 200) {
        json_error_t error;
        json_t *root = json_loadb(response->body, response->body_length, 0, &error);
        json_t *until = root ? json_object_get(root, "live_until") : NULL;
        json_t *storage = root ? json_object_get(root, "storage_path") : NULL;

        if (json_is_integer(until) && json_is_string(storage) && media_path_safe(json_string_value(storage))) {
            result->outcome = LOOKUP_LIVE;
            result->live_until = (int64_t)json_integer_value(until);

            snprintf(result->storage, sizeof(result->storage), "%s", json_string_value(storage));
        }

        json_decref(root);
    }

    int status = response->status;

    free(response);

    if (result->outcome == LOOKUP_ERROR) {
        pthread_mutex_lock(&lock);

        int64_t now = now_ms();
        int report = now - last_error_log_ms > 60000;

        if (report) {
            last_error_log_ms = now;
        }

        pthread_mutex_unlock(&lock);

        if (report) {
            say("the API at %s:%d could not tell whether a session is alive (status %d)", config.api_host, config.api_port, status);
        }
    }
}

static void *lookup_main(void *argument) {
    (void)argument;

    for (;;) {
        pthread_mutex_lock(&lock);

        while (queue_count == 0 && !stopping) {
            pthread_cond_wait(&wake, &lock);
        }

        if (stopping) {
            pthread_mutex_unlock(&lock);

            break;
        }

        int slot = queue[queue_head];
        unsigned char session[MEDIA_SESSION_BYTES];

        queue_head = (queue_head + 1) % QUEUE_MAX;
        queue_count--;

        memcpy(session, cache[slot].session, MEDIA_SESSION_BYTES);

        pthread_mutex_unlock(&lock);

        Entry result;

        fetch(session, &result);

        pthread_mutex_lock(&lock);

        Entry *entry = &cache[slot];

        if (entry->state == ENTRY_PENDING && memcmp(entry->session, session, MEDIA_SESSION_BYTES) == 0) {
            entry->state = ENTRY_READY;
            entry->outcome = result.outcome;
            entry->live_until = result.live_until;
            entry->fetched_ms = now_ms();

            memcpy(entry->storage, result.storage, sizeof(entry->storage));
        }

        pthread_mutex_unlock(&lock);

        uint64_t one = 1;
        ssize_t wrote = write(lookup_fd, &one, sizeof(one));

        (void)wrote;
    }

    return NULL;
}

static void client_close(Client *client) {
    if (client->file_fd >= 0) {
        close(client->file_fd);
    }

    epoll_ctl(epoll_fd, EPOLL_CTL_DEL, client->fd, NULL);
    close(client->fd);

    clients[client->slot] = NULL;
    stats.clients--;

    free(client);
}

static void client_drain(Client *client) {
    shutdown(client->fd, SHUT_WR);

    client->state = CLIENT_DRAIN;
    client->drain_since_ms = now_ms();

    client_interest(client, EPOLLIN);
}

static void count_status(int status) {
    switch (status) {
        case 200: stats.ok++; break;
        case 206: stats.partial++; break;
        case 403: stats.forbidden++; break;
        case 404: stats.not_found++; break;
        case 410: stats.gone++; break;
        case 416: stats.unsatisfiable++; break;
        case 503: stats.unavailable++; break;
        default: stats.bad++; break;
    }
}

static const char *reason(int status) {
    switch (status) {
        case 200: return "OK";
        case 206: return "Partial Content";
        case 400: return "Bad Request";
        case 403: return "Forbidden";
        case 404: return "Not Found";
        case 405: return "Method Not Allowed";
        case 410: return "Gone";
        case 416: return "Range Not Satisfiable";
        case 431: return "Request Header Fields Too Large";
        case 503: return "Service Unavailable";
        default: return "Error";
    }
}

static void start_send(Client *client) {
    client->state = CLIENT_SEND;
    client->head_sent = 0;
}

static void reply_text(Client *client, int status, const char *extra, const char *body) {
    size_t body_length = strlen(body);
    int length = snprintf(client->head, sizeof(client->head),
                          "HTTP/1.1 %d %s\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: %zu\r\n"
                          "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n%sConnection: %s\r\n\r\n%s",
                          status, reason(status), client->head_only ? (size_t)0 : body_length, extra ? extra : "",
                          client->keep_alive ? "keep-alive" : "close", client->head_only ? "" : body);

    client->head_length = length > 0 && (size_t)length < sizeof(client->head) ? (size_t)length : 0;
    client->file_left = 0;

    count_status(status);
    start_send(client);
}

static void reply_json(Client *client, const char *body) {
    int length = snprintf(client->head, sizeof(client->head),
                          "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %zu\r\n"
                          "Cache-Control: no-store\r\nConnection: %s\r\n\r\n%s",
                          strlen(body), client->keep_alive ? "keep-alive" : "close", client->head_only ? "" : body);

    client->head_length = length > 0 && (size_t)length < sizeof(client->head) ? (size_t)length : 0;
    client->file_left = 0;

    count_status(200);
    start_send(client);
}

static void reply_stats(Client *client) {
    char body[1024];

    snprintf(body, sizeof(body),
             "{\"service\":\"caligo-vod\",\"uptime_seconds\":%lld,\"clients\":%d,\"peak_clients\":%d,"
             "\"accepted\":%llu,\"refused\":%llu,\"requests\":%llu,\"ok\":%llu,\"partial\":%llu,"
             "\"forbidden\":%llu,\"gone\":%llu,\"not_found\":%llu,\"unavailable\":%llu,\"unsatisfiable\":%llu,"
             "\"bad\":%llu,\"bytes_out\":%llu,\"lookups\":%llu,\"lookup_errors\":%llu,\"cache_hits\":%llu}",
             (long long)((now_ms() - stats.started_ms) / 1000), stats.clients, stats.peak_clients,
             (unsigned long long)stats.accepted, (unsigned long long)stats.refused, (unsigned long long)stats.requests,
             (unsigned long long)stats.ok, (unsigned long long)stats.partial, (unsigned long long)stats.forbidden,
             (unsigned long long)stats.gone, (unsigned long long)stats.not_found, (unsigned long long)stats.unavailable,
             (unsigned long long)stats.unsatisfiable, (unsigned long long)stats.bad, (unsigned long long)stats.bytes_out,
             (unsigned long long)stats.lookups, (unsigned long long)stats.lookup_errors, (unsigned long long)stats.cache_hits);

    reply_json(client, body);
}

static int open_beneath(const char *path) {
    struct open_how how;

    memset(&how, 0, sizeof(how));

    how.flags = O_RDONLY | O_CLOEXEC | O_NOFOLLOW;
    how.resolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS;

    long fd = syscall(SYS_openat2, library_fd, path, &how, sizeof(how));

    if (fd >= 0) {
        return (int)fd;
    }

    if (errno != ENOSYS && errno != EPERM) {
        return -1;
    }

    return openat(library_fd, path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
}

static void serve_file(Client *client, const char *storage) {
    char path[MEDIA_PATH_MAX * 2 + 2];
    struct stat info;

    snprintf(path, sizeof(path), "%s/%s", storage, client->media);

    int fd = open_beneath(path);

    if (fd < 0 || fstat(fd, &info) != 0 || !S_ISREG(info.st_mode)) {
        if (fd >= 0) {
            close(fd);
        }

        reply_text(client, 404, NULL, "Not found.\n");

        return;
    }

    uint64_t size = (uint64_t)info.st_size;
    uint64_t start = 0;
    uint64_t length = size;
    RangeResult range = client->has_range ? http_byte_range(client->range, size, &start, &length) : RANGE_NONE;
    const char *type = media_content_type(client->media);

    if (range == RANGE_UNSATISFIABLE) {
        char extra[96];

        close(fd);
        snprintf(extra, sizeof(extra), "Content-Range: bytes */%llu\r\n", (unsigned long long)size);
        reply_text(client, 416, extra, "");

        return;
    }

    int partial = range == RANGE_PARTIAL;
    char content_range[128] = "";

    if (partial) {
        snprintf(content_range, sizeof(content_range), "Content-Range: bytes %llu-%llu/%llu\r\n",
                 (unsigned long long)start, (unsigned long long)(start + length - 1), (unsigned long long)size);
    }

    int head_length = snprintf(client->head, sizeof(client->head),
                               "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %llu\r\n%s"
                               "Accept-Ranges: bytes\r\nCache-Control: private, no-store\r\n"
                               "X-Content-Type-Options: nosniff\r\nConnection: %s\r\n\r\n",
                               partial ? 206 : 200, reason(partial ? 206 : 200), type, (unsigned long long)length,
                               content_range, client->keep_alive ? "keep-alive" : "close");

    if (head_length <= 0 || (size_t)head_length >= sizeof(client->head)) {
        close(fd);
        reply_text(client, 404, NULL, "Not found.\n");

        return;
    }

    client->head_length = (size_t)head_length;

    if (client->head_only || length == 0) {
        close(fd);

        client->file_fd = -1;
        client->file_left = 0;
    }
    else {
        client->file_fd = fd;
        client->file_offset = (off_t)start;
        client->file_left = length;
    }

    count_status(partial ? 206 : 200);
    start_send(client);
}

static void decide(Client *client, const Entry *entry) {
    if (entry->outcome == LOOKUP_ERROR) {
        reply_text(client, 503, "Retry-After: 2\r\n", "The session service is not answering.\n");

        return;
    }

    if (entry->outcome != LOOKUP_LIVE || entry->live_until <= wall_seconds() ||
        media_scope(entry->storage) != client->grant.scope) {
        reply_text(client, 410, NULL, "This playback session has ended.\n");

        return;
    }

    char storage[MEDIA_PATH_MAX + 1];

    memcpy(storage, entry->storage, sizeof(storage));
    serve_file(client, storage);
}

static void lookup(Client *client) {
    int64_t now = now_ms();

    pthread_mutex_lock(&lock);

    int slot = cache_slot(client->grant.session, now);

    if (slot < 0) {
        pthread_mutex_unlock(&lock);
        reply_text(client, 503, "Retry-After: 1\r\n", "Too many sessions are being checked at once.\n");

        return;
    }

    Entry *entry = &cache[slot];

    entry->used_ms = now;

    if (entry_fresh(entry, now)) {
        Entry copy = *entry;

        stats.cache_hits++;
        pthread_mutex_unlock(&lock);
        decide(client, &copy);

        return;
    }

    if (entry->state != ENTRY_PENDING) {
        if (queue_count == QUEUE_MAX) {
            pthread_mutex_unlock(&lock);
            reply_text(client, 503, "Retry-After: 1\r\n", "Too many sessions are being checked at once.\n");

            return;
        }

        entry->state = ENTRY_PENDING;
        queue[(queue_head + queue_count) % QUEUE_MAX] = slot;
        queue_count++;
        stats.lookups++;

        pthread_cond_signal(&wake);
    }

    pthread_mutex_unlock(&lock);

    client->state = CLIENT_WAIT;
    client->entry = slot;
    client->waiting_since_ms = now;

    client_interest(client, 0);
}

static void resume_waiting(void) {
    int64_t now = now_ms();

    for (int index = 0; index < config.max_clients; index++) {
        Client *client = clients[index];

        if (!client || client->state != CLIENT_WAIT) {
            continue;
        }

        pthread_mutex_lock(&lock);

        Entry *entry = &cache[client->entry];
        int matches = memcmp(entry->session, client->grant.session, MEDIA_SESSION_BYTES) == 0;
        int ready = matches && entry->state == ENTRY_READY;
        Entry copy;

        if (ready) {
            copy = *entry;

            if (copy.outcome == LOOKUP_ERROR) {
                stats.lookup_errors++;
            }
        }

        pthread_mutex_unlock(&lock);

        if (ready) {
            decide(client, &copy);
        }
        else if (!matches) {
            lookup(client);
        }
        else if (now - client->waiting_since_ms > config.lookup_timeout_ms + 2000) {
            reply_text(client, 503, "Retry-After: 2\r\n", "The session service is not answering.\n");
        }

        if (clients[index] == client && client->state == CLIENT_SEND) {
            client_flush(client);
        }
    }
}

static int header_present(const char *headers, const char *name) {
    char value[64];

    return http_header_value(headers, name, value, sizeof(value));
}

static void handle_request(Client *client) {
    HttpRequest request;
    char copy[REQUEST_MAX + 1];

    memcpy(copy, client->in, client->request_length);
    copy[client->request_length] = '\0';

    const char *line_end = strstr(copy, "\r\n");

    stats.requests++;
    client->keep_alive = 0;
    client->head_only = 0;
    client->has_range = 0;
    client->file_fd = -1;

    if (!line_end || memchr(copy, '\0', client->request_length) != NULL ||
        http_parse_request(copy, client->request_length, &request) != 0) {
        reply_text(client, 400, NULL, "Bad request.\n");

        return;
    }

    const char *headers = line_end + 2;
    char value[128];

    client->head_only = strcmp(request.method, "HEAD") == 0;

    if (request.version_minor == 1) {
        client->keep_alive = !(http_header_value(headers, "Connection", value, sizeof(value)) && strcasestr(value, "close"));
    }

    if ((http_header_value(headers, "Content-Length", value, sizeof(value)) && strcmp(value, "0") != 0) ||
        header_present(headers, "Transfer-Encoding")) {
        client->keep_alive = 0;
        reply_text(client, 400, NULL, "Requests to this server carry no body.\n");

        return;
    }

    if (strcmp(request.method, "GET") != 0 && !client->head_only) {
        reply_text(client, 405, "Allow: GET, HEAD\r\n", "Only GET and HEAD.\n");

        return;
    }

    const char *path = request.path;

    if (strncmp(path, "/vod/", 5) == 0) {
        path += 4;
    }

    if (strcmp(path, "/healthz") == 0) {
        reply_text(client, 200, NULL, "ok\n");

        return;
    }

    if (strcmp(path, "/stats") == 0) {
        reply_stats(client);

        return;
    }

    const char *token = path + 1;
    const char *slash = strchr(token, '/');

    if (*path != '/' || !slash) {
        reply_text(client, 404, NULL, "Not found.\n");

        return;
    }

    const char *media = slash + 1;

    if (!media_path_safe(media) || !media_content_type(media)) {
        reply_text(client, 404, NULL, "Not found.\n");

        return;
    }

    if (media_token_read(config.key, config.key_length, token, (size_t)(slash - token), &client->grant) != 0) {
        reply_text(client, 403, NULL, "This address is not signed for playback.\n");

        return;
    }

    if (client->grant.expires <= wall_seconds()) {
        reply_text(client, 410, NULL, "This playback session has ended.\n");

        return;
    }

    snprintf(client->media, sizeof(client->media), "%s", media);

    if (http_header_value(headers, "Range", client->range, sizeof(client->range))) {
        client->has_range = 1;
    }

    lookup(client);
}

static void client_next(Client *client) {
    size_t used = client->request_length;

    if (used > 0 && used <= client->in_length) {
        memmove(client->in, client->in + used, client->in_length - used);

        client->in_length -= used;
    }

    client->request_length = 0;
    client->state = CLIENT_READ;
    client->activity_ms = now_ms();

    const char *end = client->in_length >= 4 ? memmem(client->in, client->in_length, "\r\n\r\n", 4) : NULL;

    if (end) {
        client->request_length = (size_t)(end - client->in) + 4;

        handle_request(client);

        if (client->state == CLIENT_SEND) {
            client_flush(client);
        }

        return;
    }

    client_interest(client, EPOLLIN);
}

static void client_flush(Client *client) {
    while (client->head_sent < client->head_length) {
        ssize_t sent = send(client->fd, client->head + client->head_sent, client->head_length - client->head_sent, MSG_NOSIGNAL);

        if (sent > 0) {
            client->head_sent += (size_t)sent;
            client->activity_ms = now_ms();
            stats.bytes_out += (uint64_t)sent;

            continue;
        }

        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            client_interest(client, EPOLLOUT);

            return;
        }

        if (sent < 0 && errno == EINTR) {
            continue;
        }

        client_close(client);

        return;
    }

    while (client->file_left > 0) {
        size_t want = client->file_left < SEND_CHUNK ? (size_t)client->file_left : SEND_CHUNK;
        ssize_t sent = sendfile(client->fd, client->file_fd, &client->file_offset, want);

        if (sent > 0) {
            client->file_left -= (uint64_t)sent;
            client->activity_ms = now_ms();
            stats.bytes_out += (uint64_t)sent;

            continue;
        }

        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            client_interest(client, EPOLLOUT);

            return;
        }

        if (sent < 0 && errno == EINTR) {
            continue;
        }

        client_close(client);

        return;
    }

    if (client->file_fd >= 0) {
        close(client->file_fd);

        client->file_fd = -1;
    }

    if (client->keep_alive) {
        client_next(client);
    }
    else {
        client_drain(client);
    }
}

static void client_readable(Client *client) {
    for (;;) {
        if (client->state == CLIENT_DRAIN) {
            char discard[4096];
            ssize_t got = recv(client->fd, discard, sizeof(discard), 0);

            if (got > 0) {
                continue;
            }

            if (got < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
                return;
            }

            client_close(client);

            return;
        }

        if (client->in_length == REQUEST_MAX) {
            break;
        }

        ssize_t got = recv(client->fd, client->in + client->in_length, REQUEST_MAX - client->in_length, 0);

        if (got > 0) {
            client->in_length += (size_t)got;
            client->activity_ms = now_ms();

            continue;
        }

        if (got == 0) {
            client_close(client);

            return;
        }

        if (errno == EINTR) {
            continue;
        }

        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            break;
        }

        client_close(client);

        return;
    }

    if (client->state != CLIENT_READ) {
        return;
    }

    const char *end = client->in_length >= 4 ? memmem(client->in, client->in_length, "\r\n\r\n", 4) : NULL;

    if (!end) {
        if (client->in_length == REQUEST_MAX) {
            client->keep_alive = 0;
            client->head_only = 0;
            client->request_length = client->in_length;
            reply_text(client, 431, NULL, "The request head is too large.\n");
            client_flush(client);
        }

        return;
    }

    client->request_length = (size_t)(end - client->in) + 4;

    handle_request(client);

    if (client->state == CLIENT_SEND) {
        client_flush(client);
    }
}

static void accept_clients(void) {
    for (;;) {
        int fd = accept4(listen_fd, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);

        if (fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            return;
        }

        int slot = -1;

        for (int index = 0; index < config.max_clients; index++) {
            if (!clients[index]) {
                slot = index;

                break;
            }
        }

        Client *client = slot >= 0 ? calloc(1, sizeof(Client)) : NULL;

        if (!client) {
            static const char busy[] = "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nRetry-After: 2\r\nConnection: close\r\n\r\n";
            ssize_t sent = send(fd, busy, sizeof(busy) - 1, MSG_NOSIGNAL);

            (void)sent;
            close(fd);
            stats.refused++;

            continue;
        }

        client->kind = KIND_CLIENT;
        client->fd = fd;
        client->slot = slot;
        client->state = CLIENT_READ;
        client->file_fd = -1;
        client->interest = EPOLLIN;
        client->activity_ms = now_ms();

        clients[slot] = client;
        stats.accepted++;
        stats.clients++;

        if (stats.clients > stats.peak_clients) {
            stats.peak_clients = stats.clients;
        }

        watch(fd, EPOLLIN, client, EPOLL_CTL_ADD);
    }
}

static void sweep(void) {
    int64_t now = now_ms();

    for (int index = 0; index < config.max_clients; index++) {
        Client *client = clients[index];

        if (!client) {
            continue;
        }

        int64_t quiet = now - client->activity_ms;

        if ((client->state == CLIENT_READ && client->in_length > 0 && quiet > config.request_timeout * 1000LL) ||
            (client->state == CLIENT_READ && client->in_length == 0 && quiet > config.idle_timeout * 1000LL) ||
            (client->state == CLIENT_SEND && quiet > config.write_timeout * 1000LL) ||
            (client->state == CLIENT_DRAIN && now - client->drain_since_ms > DRAIN_MS)) {
            client_close(client);
        }
    }
}

static int open_listener(void) {
    struct addrinfo hints;
    struct addrinfo *found = NULL;
    char port[16];

    memset(&hints, 0, sizeof(hints));

    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST;

    snprintf(port, sizeof(port), "%d", config.listen_port);

    if (getaddrinfo(config.listen_host, port, &hints, &found) != 0 || !found) {
        say("VOD_LISTEN_HOST=%s is not an address", config.listen_host);

        return -1;
    }

    int fd = socket(found->ai_family, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    int enabled = 1;

    if (fd >= 0) {
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    }

    if (fd < 0 || bind(fd, found->ai_addr, found->ai_addrlen) < 0 || listen(fd, 1024) < 0) {
        say("cannot listen on %s:%d: %s", config.listen_host, config.listen_port, strerror(errno));

        if (fd >= 0) {
            close(fd);
        }

        freeaddrinfo(found);

        return -1;
    }

    freeaddrinfo(found);

    return fd;
}

static void on_signal(int number) {
    (void)number;

    running = 0;
}

int main(void) {
    setvbuf(stderr, NULL, _IOLBF, 0);

    if (load_config() != 0) {
        return 2;
    }

    signal(SIGPIPE, SIG_IGN);
    signal(SIGTERM, on_signal);
    signal(SIGINT, on_signal);

    library_fd = open(config.library, O_RDONLY | O_DIRECTORY | O_CLOEXEC);

    if (library_fd < 0) {
        say("cannot open the media library %s: %s", config.library, strerror(errno));

        return 1;
    }

    clients = calloc((size_t)config.max_clients, sizeof(Client *));
    epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    listen_fd = open_listener();
    lookup_fd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);

    if (!clients || epoll_fd < 0 || listen_fd < 0 || lookup_fd < 0) {
        return 1;
    }

    for (int index = 0; index < config.lookup_threads; index++) {
        if (pthread_create(&threads[index], NULL, lookup_main, NULL) != 0) {
            say("cannot start the session lookup threads");

            return 1;
        }
    }

    watch(listen_fd, EPOLLIN, &listen_kind, EPOLL_CTL_ADD);
    watch(lookup_fd, EPOLLIN, &lookup_kind, EPOLL_CTL_ADD);

    stats.started_ms = now_ms();

    say("listening on %s:%d, library %s, sessions from %s:%d%s",
        config.listen_host, config.listen_port, config.library, config.api_host, config.api_port, config.api_path);

    int64_t last_sweep = 0;

    while (running) {
        struct epoll_event ready[256];
        int count = epoll_wait(epoll_fd, ready, 256, 250);

        if (count < 0 && errno != EINTR) {
            say("epoll_wait failed: %s", strerror(errno));

            break;
        }

        for (int index = 0; index < count; index++) {
            int kind = *(int *)ready[index].data.ptr;
            uint32_t happened = ready[index].events;

            if (kind == KIND_LISTEN) {
                accept_clients();
            }
            else if (kind == KIND_LOOKUP) {
                uint64_t ignored;
                ssize_t got = read(lookup_fd, &ignored, sizeof(ignored));

                (void)got;
                resume_waiting();
            }
            else {
                Client *client = ready[index].data.ptr;
                int slot = client->slot;

                if (happened & (EPOLLERR | EPOLLHUP)) {
                    client_close(client);

                    continue;
                }

                if (happened & EPOLLIN) {
                    client_readable(client);

                    if (clients[slot] != client) {
                        continue;
                    }
                }

                if ((happened & EPOLLOUT) && client->state == CLIENT_SEND) {
                    client_flush(client);
                }
            }
        }

        int64_t now = now_ms();

        if (now - last_sweep >= 1000) {
            last_sweep = now;

            sweep();
            resume_waiting();
        }
    }

    pthread_mutex_lock(&lock);
    stopping = 1;
    pthread_cond_broadcast(&wake);
    pthread_mutex_unlock(&lock);

    for (int index = 0; index < config.lookup_threads; index++) {
        pthread_join(threads[index], NULL);
    }

    say("stopped");

    return 0;
}
