#define _GNU_SOURCE

#include "client.h"
#include "ladder.h"
#include "media.h"
#include "playlist.h"
#include "probe.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <ftw.h>
#include <jansson.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define MAX_SUBTITLES 32
#define MAX_ARGS 256
#define ERROR_MAX 2000
#define FILE_LIMIT (16 * 1024 * 1024)
#define PROBE_LIMIT (4 * 1024 * 1024)
#define API_TIMEOUT_MS 15000

extern char **environ;

typedef struct {
    char api_host[256];
    int api_port;
    unsigned char key[512];
    size_t key_length;
    char internal_key[MEDIA_INTERNAL_KEY_HEX + 1];
    char incoming[512];
    char library[512];
    char ffmpeg[256];
    char ffprobe[256];
    char preset[32];
    char heartbeat[512];
    Rung rungs[LADDER_MAX];
    int rung_count;
    int segment_seconds;
    int audio_kbps;
    int progressive_height;
    int poll_seconds;
    int job_timeout;
    int threads;
    long subtitle_max_bytes;
} Config;

typedef struct {
    char file[256];
    int language_id;
    char language[64];
    char tag[16];
} JobSubtitle;

typedef struct {
    int content_id;
    char source[256];
    int attempts;
    char storage[MEDIA_PATH_MAX + 1];
    int claim_seconds;
    JobSubtitle subtitles[MAX_SUBTITLES];
    int subtitle_count;
} Job;

typedef struct {
    char *items[MAX_ARGS + 1];
    int count;
    int failed;
} Args;

typedef enum { RUN_OK, RUN_FAILED, RUN_TIMEOUT, RUN_STOPPED, RUN_LOST } RunOutcome;

typedef struct {
    char *out;
    size_t out_length;
    char tail[4096];
    size_t tail_length;
    int status;
} Run;

static Config config;
static volatile sig_atomic_t running = 1;

static void say(const char *format, ...) {
    char text[1024];
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);

    fprintf(stderr, "[packager] %s\n", text);
}

static int64_t now_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
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
    snprintf(config.api_host, sizeof(config.api_host), "%s", env_text("API_HOST", "caligo-api"));
    snprintf(config.incoming, sizeof(config.incoming), "%s", env_text("PACKAGER_INCOMING", "/media/incoming"));
    snprintf(config.library, sizeof(config.library), "%s", env_text("PACKAGER_LIBRARY", "/media/library"));
    snprintf(config.ffmpeg, sizeof(config.ffmpeg), "%s", env_text("PACKAGER_FFMPEG", "ffmpeg"));
    snprintf(config.ffprobe, sizeof(config.ffprobe), "%s", env_text("PACKAGER_FFPROBE", "ffprobe"));
    snprintf(config.preset, sizeof(config.preset), "%s", env_text("PACKAGER_PRESET", "veryfast"));
    snprintf(config.heartbeat, sizeof(config.heartbeat), "%s", env_text("PACKAGER_HEARTBEAT", ""));

    config.api_port = (int)env_number("API_PORT", 8080, 1, 65535);
    config.segment_seconds = (int)env_number("PACKAGER_SEGMENT_SECONDS", 4, 1, 30);
    config.audio_kbps = (int)env_number("PACKAGER_AUDIO_KBPS", 128, 32, 512);
    config.progressive_height = (int)env_number("PACKAGER_PROGRESSIVE_HEIGHT", 480, 144, 4320);
    config.poll_seconds = (int)env_number("PACKAGER_POLL_SECONDS", 5, 1, 3600);
    config.job_timeout = (int)env_number("PACKAGER_JOB_TIMEOUT", 6 * 3600, 60, 7 * 24 * 3600);
    config.threads = (int)env_number("PACKAGER_THREADS", 0, 0, 64);
    config.subtitle_max_bytes = env_number("PACKAGER_SUBTITLE_MAX_BYTES", 5 * 1024 * 1024, 1024, 64 * 1024 * 1024);

    config.rung_count = ladder_parse(env_text("PACKAGER_LADDER", "1080:5000,720:2800,480:1400,360:800,240:400"), config.rungs, LADDER_MAX);

    if (config.rung_count <= 0) {
        say("PACKAGER_LADDER must look like 1080:5000,720:2800 (height:kbps, up to %d rungs)", LADDER_MAX);

        return -1;
    }

    if (!media_path_safe(config.preset)) {
        say("PACKAGER_PRESET=%s is not an x264 preset name", config.preset);

        return -1;
    }

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

static int join_path(char *out, size_t capacity, const char *first, const char *second) {
    size_t first_length = strlen(first);
    size_t second_length = strlen(second);

    if (first_length + second_length + 2 > capacity) {
        if (capacity > 0) {
            out[0] = '\0';
        }

        return -1;
    }

    memcpy(out, first, first_length);
    out[first_length] = '/';
    memcpy(out + first_length + 1, second, second_length);
    out[first_length + 1 + second_length] = '\0';

    return 0;
}

static void message(char *out, size_t capacity, const char *format, ...) {
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(out, capacity, format, arguments);
    va_end(arguments);
}

static void touch_heartbeat(void) {
    if (!config.heartbeat[0]) {
        return;
    }

    int fd = open(config.heartbeat, O_WRONLY | O_CREAT | O_CLOEXEC, 0644);

    if (fd >= 0) {
        futimens(fd, NULL);
        close(fd);
    }
}

static void args_add(Args *args, const char *format, ...) {
    if (args->failed || args->count == MAX_ARGS) {
        args->failed = 1;

        return;
    }

    va_list arguments;
    char *text = NULL;

    va_start(arguments, format);

    int length = vasprintf(&text, format, arguments);

    va_end(arguments);

    if (length < 0) {
        args->failed = 1;

        return;
    }

    args->items[args->count++] = text;
    args->items[args->count] = NULL;
}

static void args_free(Args *args) {
    for (int i = 0; i < args->count; i++) {
        free(args->items[i]);
    }

    args->count = 0;
}

static int api_post(const char *path, json_t *body, ClientResponse *response) {
    char headers[160];
    char *text = body ? json_dumps(body, JSON_COMPACT) : NULL;

    snprintf(headers, sizeof(headers), "X-Caligo-Key: %s\r\n", config.internal_key);

    int result = client_call(config.api_host, config.api_port, "POST", path, headers,
                             text ? text : "", text ? strlen(text) : 0, API_TIMEOUT_MS, response);

    free(text);

    return result;
}

static json_t *claim_reference(const Job *job) {
    return json_pack("{s:i, s:s}", "attempts", job->attempts, "source_file", job->source);
}

static int extend_claim(const Job *job) {
    ClientResponse *response = malloc(sizeof(*response));
    json_t *body = claim_reference(job);
    char path[128];
    int result = 0;

    snprintf(path, sizeof(path), "/internal/v1/packaging/%d/extend", job->content_id);

    if (response && body && api_post(path, body, response) == 0 && response->status == 409) {
        result = -1;
    }

    json_decref(body);
    free(response);

    return result;
}

static void stop_child(pid_t pid) {
    int status;

    kill(pid, SIGTERM);

    for (int waited = 0; waited < 50; waited++) {
        if (waitpid(pid, &status, WNOHANG) == pid) {
            return;
        }

        usleep(100000);
    }

    kill(pid, SIGKILL);
    waitpid(pid, &status, 0);
}

static void keep_tail(Run *run, const char *data, size_t length) {
    size_t capacity = sizeof(run->tail) - 1;

    if (length >= capacity) {
        memcpy(run->tail, data + length - capacity, capacity);

        run->tail_length = capacity;
    }
    else {
        if (run->tail_length + length > capacity) {
            size_t drop = run->tail_length + length - capacity;

            memmove(run->tail, run->tail + drop, run->tail_length - drop);

            run->tail_length -= drop;
        }

        memcpy(run->tail + run->tail_length, data, length);

        run->tail_length += length;
    }

    run->tail[run->tail_length] = '\0';
}

static RunOutcome run_tool(Args *args, Run *run, const Job *job, int capture) {
    int out_pipe[2] = { -1, -1 };
    int err_pipe[2] = { -1, -1 };
    posix_spawn_file_actions_t actions;
    pid_t pid;

    memset(run, 0, sizeof(*run));

    if (args->failed || pipe2(err_pipe, O_CLOEXEC) != 0 || (capture && pipe2(out_pipe, O_CLOEXEC) != 0)) {
        snprintf(run->tail, sizeof(run->tail), "could not prepare to run %s", args->items[0]);

        return RUN_FAILED;
    }

    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0);
    posix_spawn_file_actions_adddup2(&actions, err_pipe[1], 2);
    posix_spawn_file_actions_adddup2(&actions, capture ? out_pipe[1] : err_pipe[1], 1);

    int spawned = posix_spawnp(&pid, args->items[0], &actions, NULL, args->items, environ);

    posix_spawn_file_actions_destroy(&actions);
    close(err_pipe[1]);

    if (capture) {
        close(out_pipe[1]);
    }

    if (spawned != 0) {
        close(err_pipe[0]);

        if (capture) {
            close(out_pipe[0]);
        }

        snprintf(run->tail, sizeof(run->tail), "could not start %s: %s", args->items[0], strerror(spawned));

        return RUN_FAILED;
    }

    size_t out_capacity = 0;
    int out_open = capture;
    int err_open = 1;
    int64_t started = now_ms();
    int64_t last_extend = started;
    RunOutcome outcome = RUN_OK;
    char buffer[16384];

    while (out_open || err_open) {
        struct pollfd fds[2];
        int count = 0;

        if (err_open) {
            fds[count].fd = err_pipe[0];
            fds[count].events = POLLIN;
            fds[count].revents = 0;
            count++;
        }

        if (out_open) {
            fds[count].fd = out_pipe[0];
            fds[count].events = POLLIN;
            fds[count].revents = 0;
            count++;
        }

        int ready = poll(fds, (nfds_t)count, 1000);

        for (int i = 0; ready > 0 && i < count; i++) {
            if (!(fds[i].revents & (POLLIN | POLLHUP | POLLERR))) {
                continue;
            }

            ssize_t got = read(fds[i].fd, buffer, sizeof(buffer));

            if (got <= 0) {
                if (got < 0 && errno == EINTR) {
                    continue;
                }

                if (fds[i].fd == err_pipe[0]) {
                    err_open = 0;
                }
                else {
                    out_open = 0;
                }

                continue;
            }

            if (fds[i].fd == err_pipe[0]) {
                keep_tail(run, buffer, (size_t)got);
            }
            else if (run->out_length + (size_t)got > PROBE_LIMIT) {
                outcome = RUN_FAILED;
            }
            else {
                if (run->out_length + (size_t)got + 1 > out_capacity) {
                    size_t capacity = out_capacity ? out_capacity * 2 : 65536;

                    while (capacity < run->out_length + (size_t)got + 1) {
                        capacity *= 2;
                    }

                    char *grown = realloc(run->out, capacity);

                    if (!grown) {
                        outcome = RUN_FAILED;

                        break;
                    }

                    run->out = grown;
                    out_capacity = capacity;
                }

                memcpy(run->out + run->out_length, buffer, (size_t)got);

                run->out_length += (size_t)got;
                run->out[run->out_length] = '\0';
            }
        }

        int64_t now = now_ms();

        if (outcome == RUN_OK && !running) {
            outcome = RUN_STOPPED;
        }

        if (outcome == RUN_OK && now - started > config.job_timeout * 1000LL) {
            outcome = RUN_TIMEOUT;
        }

        if (outcome == RUN_OK && job && now - last_extend > job->claim_seconds * 1000LL / 3) {
            last_extend = now;

            touch_heartbeat();

            if (extend_claim(job) != 0) {
                outcome = RUN_LOST;
            }
        }

        if (outcome != RUN_OK) {
            break;
        }
    }

    close(err_pipe[0]);

    if (capture) {
        close(out_pipe[0]);
    }

    if (outcome != RUN_OK) {
        stop_child(pid);

        return outcome;
    }

    int status = 0;

    while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
    }

    run->status = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + (WIFSIGNALED(status) ? WTERMSIG(status) : 0);

    return run->status == 0 ? RUN_OK : RUN_FAILED;
}

static void run_free(Run *run) {
    free(run->out);

    run->out = NULL;
}

static void describe_failure(const char *what, RunOutcome outcome, const Run *run, char *error, size_t capacity) {
    const char *tail = run->tail;
    size_t length = strlen(tail);

    while (length > 0 && (tail[length - 1] == '\n' || tail[length - 1] == '\r')) {
        length--;
    }

    if (length > 600) {
        tail += length - 600;
        length = 600;
    }

    switch (outcome) {
        case RUN_TIMEOUT:
            snprintf(error, capacity, "%s took longer than %d seconds.", what, config.job_timeout);
            break;
        case RUN_STOPPED:
            snprintf(error, capacity, "%s was stopped because the packager is shutting down.", what);
            break;
        case RUN_LOST:
            snprintf(error, capacity, "%s was stopped because the job was taken away.", what);
            break;
        default:
            snprintf(error, capacity, "%s failed (exit %d): %.*s", what, run->status, (int)length, tail);
            break;
    }
}

static char *read_file(const char *path, size_t limit, size_t *length) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    struct stat info;

    if (fd < 0 || fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || (size_t)info.st_size > limit) {
        if (fd >= 0) {
            close(fd);
        }

        return NULL;
    }

    size_t size = (size_t)info.st_size;
    char *data = malloc(size + 1);
    size_t done = 0;

    while (data && done < size) {
        ssize_t got = read(fd, data + done, size - done);

        if (got <= 0) {
            free(data);
            data = NULL;

            break;
        }

        done += (size_t)got;
    }

    close(fd);

    if (data) {
        data[size] = '\0';

        if (length) {
            *length = size;
        }
    }

    return data;
}

static int write_file(const char *path, const char *data) {
    char temporary[1100];

    snprintf(temporary, sizeof(temporary), "%s.part", path);

    int fd = open(temporary, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);

    if (fd < 0) {
        return -1;
    }

    size_t length = strlen(data);
    size_t done = 0;

    while (done < length) {
        ssize_t wrote = write(fd, data + done, length - done);

        if (wrote <= 0) {
            close(fd);
            unlink(temporary);

            return -1;
        }

        done += (size_t)wrote;
    }

    if (close(fd) != 0 || rename(temporary, path) != 0) {
        unlink(temporary);

        return -1;
    }

    return 0;
}

static uint64_t file_size(const char *path) {
    struct stat info;

    return stat(path, &info) == 0 ? (uint64_t)info.st_size : 0;
}

static int make_directory(const char *path) {
    char partial[1024];
    size_t length = strlen(path);

    if (length >= sizeof(partial)) {
        return -1;
    }

    for (size_t i = 1; i <= length; i++) {
        if (path[i] == '/' || path[i] == '\0') {
            memcpy(partial, path, i);
            partial[i] = '\0';

            if (mkdir(partial, 0755) != 0 && errno != EEXIST) {
                return -1;
            }
        }
    }

    return 0;
}

static int remove_entry(const char *path, const struct stat *info, int flag, struct FTW *walk) {
    (void)info;
    (void)walk;

    return flag == FTW_DP ? rmdir(path) : unlink(path);
}

static void remove_tree(const char *path) {
    nftw(path, remove_entry, 16, FTW_DEPTH | FTW_PHYS);
}

static void remove_orphans(const Job *job) {
    char folder[700];
    const char *keep = NULL;
    char prefix[32];

    snprintf(folder, sizeof(folder), "%s/%d", config.library, job->content_id);
    snprintf(prefix, sizeof(prefix), "%d/", job->content_id);

    if (job->storage[0] && strncmp(job->storage, prefix, strlen(prefix)) == 0) {
        keep = job->storage + strlen(prefix);
    }

    DIR *directory = opendir(folder);

    if (!directory) {
        return;
    }

    struct dirent *entry;

    while ((entry = readdir(directory)) != NULL) {
        if (entry->d_name[0] == '.' || (keep && strcmp(entry->d_name, keep) == 0)) {
            continue;
        }

        char path[1100];

        snprintf(path, sizeof(path), "%s/%s", folder, entry->d_name);
        say("removing an unfinished package %d/%s", job->content_id, entry->d_name);
        remove_tree(path);
    }

    closedir(directory);
}

static int claim(Job *job) {
    ClientResponse *response = malloc(sizeof(*response));
    int result = -1;

    memset(job, 0, sizeof(*job));

    if (!response) {
        return -1;
    }

    if (api_post("/internal/v1/packaging/claim", NULL, response) != 0) {
        free(response);

        return -1;
    }

    if (response->status == 204) {
        free(response);

        return 0;
    }

    if (response->status != 200) {
        say("the API refused to hand out work (status %d)", response->status);
        free(response);

        return -1;
    }

    json_error_t error;
    json_t *root = json_loadb(response->body, response->body_length, 0, &error);
    json_t *storage = root ? json_object_get(root, "storage_path") : NULL;
    json_t *subtitles = root ? json_object_get(root, "subtitles") : NULL;
    const char *source = root ? json_string_value(json_object_get(root, "source_file")) : NULL;

    if (root && json_is_integer(json_object_get(root, "content_id")) && source &&
        json_is_integer(json_object_get(root, "attempts"))) {
        job->content_id = (int)json_integer_value(json_object_get(root, "content_id"));
        job->attempts = (int)json_integer_value(json_object_get(root, "attempts"));
        job->claim_seconds = (int)json_integer_value(json_object_get(root, "claim_seconds"));

        if (job->claim_seconds < 3) {
            job->claim_seconds = 120;
        }

        snprintf(job->source, sizeof(job->source), "%s", source);

        if (json_is_string(storage) && media_path_safe(json_string_value(storage))) {
            snprintf(job->storage, sizeof(job->storage), "%s", json_string_value(storage));
        }

        size_t index;
        json_t *item;

        json_array_foreach(subtitles, index, item) {
            const char *file = json_string_value(json_object_get(item, "file"));
            const char *language = json_string_value(json_object_get(item, "language"));
            const char *tag = json_string_value(json_object_get(item, "tag"));
            json_t *language_id = json_object_get(item, "language_id");

            if (job->subtitle_count == MAX_SUBTITLES || !file || !language || !json_is_integer(language_id)) {
                continue;
            }

            JobSubtitle *subtitle = &job->subtitles[job->subtitle_count++];

            snprintf(subtitle->file, sizeof(subtitle->file), "%s", file);
            snprintf(subtitle->language, sizeof(subtitle->language), "%s", language);
            snprintf(subtitle->tag, sizeof(subtitle->tag), "%s", tag && media_path_safe(tag) ? tag : "");

            subtitle->language_id = (int)json_integer_value(language_id);
        }

        result = 1;
    }
    else {
        say("the API's claim answer is not a job");
    }

    json_decref(root);
    free(response);

    return result;
}

static void report_failure(const Job *job, const char *error) {
    ClientResponse *response = malloc(sizeof(*response));
    json_t *body = claim_reference(job);
    char path[128];

    say("title %d failed: %s", job->content_id, error);

    if (response && body) {
        json_object_set_new(body, "error", json_string(error));
        snprintf(path, sizeof(path), "/internal/v1/packaging/%d/failed", job->content_id);

        if (api_post(path, body, response) != 0) {
            say("could not tell the API that title %d failed", job->content_id);
        }
    }

    json_decref(body);
    free(response);
}

static void release(const Job *job) {
    ClientResponse *response = malloc(sizeof(*response));
    json_t *body = claim_reference(job);
    char path[128];

    if (response && body) {
        snprintf(path, sizeof(path), "/internal/v1/packaging/%d/release", job->content_id);
        api_post(path, body, response);
    }

    json_decref(body);
    free(response);
}

static void base_args(Args *args, const char *demuxer, const char *whitelist, const char *input) {
    args_add(args, "%s", config.ffmpeg);
    args_add(args, "-hide_banner");
    args_add(args, "-nostdin");
    args_add(args, "-nostats");
    args_add(args, "-loglevel");
    args_add(args, "error");
    args_add(args, "-y");
    args_add(args, "-protocol_whitelist");
    args_add(args, "file");
    args_add(args, "-format_whitelist");
    args_add(args, "%s", whitelist);
    args_add(args, "-f");
    args_add(args, "%s", demuxer);
    args_add(args, "-i");
    args_add(args, "%s", input);
}

static void video_encoder_args(Args *args) {
    args_add(args, "-c:v");
    args_add(args, "libx264");
    args_add(args, "-preset");
    args_add(args, "%s", config.preset);
    args_add(args, "-profile:v");
    args_add(args, "high");
    args_add(args, "-pix_fmt");
    args_add(args, "yuv420p");

    if (config.threads > 0) {
        args_add(args, "-threads");
        args_add(args, "%d", config.threads);
    }
}

static void audio_encoder_args(Args *args) {
    args_add(args, "-c:a");
    args_add(args, "aac");
    args_add(args, "-b:a");
    args_add(args, "%dk", config.audio_kbps);
    args_add(args, "-ac");
    args_add(args, "2");
    args_add(args, "-ar");
    args_add(args, "48000");
}

static int package_cmaf(const Job *job, const SourceFacts *facts, const char *source, const char *directory,
                        const Output *outputs, int output_count, char *error, size_t capacity) {
    Args args = { 0 };
    char filter[2048];
    size_t used = (size_t)snprintf(filter, sizeof(filter), "[0:v:%d]split=%d", facts->video_ordinal, output_count);

    for (int i = 0; i < output_count && used < sizeof(filter); i++) {
        used += (size_t)snprintf(filter + used, sizeof(filter) - used, "[s%d]", i);
    }

    for (int i = 0; i < output_count && used < sizeof(filter); i++) {
        used += (size_t)snprintf(filter + used, sizeof(filter) - used, ";[s%d]scale=%d:%d[o%d]",
                                 i, outputs[i].width, outputs[i].height, i);
    }

    if (used >= sizeof(filter)) {
        snprintf(error, capacity, "The filter graph is too long.");

        return -1;
    }

    base_args(&args, facts->demuxer, "mov,matroska,avi,mpegts,mpeg", source);
    args_add(&args, "-filter_complex");
    args_add(&args, "%s", filter);

    for (int i = 0; i < output_count; i++) {
        args_add(&args, "-map");
        args_add(&args, "[o%d]", i);
    }

    if (facts->has_audio) {
        args_add(&args, "-map");
        args_add(&args, "0:a:0");
    }

    args_add(&args, "-map_metadata");
    args_add(&args, "-1");
    args_add(&args, "-map_chapters");
    args_add(&args, "-1");
    args_add(&args, "-sn");
    args_add(&args, "-dn");
    video_encoder_args(&args);
    args_add(&args, "-sc_threshold");
    args_add(&args, "0");
    args_add(&args, "-force_key_frames");
    args_add(&args, "expr:gte(t,n_forced*%d)", config.segment_seconds);

    for (int i = 0; i < output_count; i++) {
        args_add(&args, "-b:v:%d", i);
        args_add(&args, "%dk", outputs[i].kbps);
        args_add(&args, "-maxrate:v:%d", i);
        args_add(&args, "%dk", outputs[i].kbps * 107 / 100);
        args_add(&args, "-bufsize:v:%d", i);
        args_add(&args, "%dk", outputs[i].kbps * 3 / 2);
    }

    if (facts->has_audio) {
        audio_encoder_args(&args);
    }

    args_add(&args, "-f");
    args_add(&args, "dash");
    args_add(&args, "-seg_duration");
    args_add(&args, "%d", config.segment_seconds);
    args_add(&args, "-use_template");
    args_add(&args, "1");
    args_add(&args, "-use_timeline");
    args_add(&args, "1");
    args_add(&args, "-format_options");
    args_add(&args, "movflags=+cmaf");
    args_add(&args, "-init_seg_name");
    args_add(&args, "init-$RepresentationID$.m4s");
    args_add(&args, "-media_seg_name");
    args_add(&args, "seg-$RepresentationID$-$Number%%05d$.m4s");
    args_add(&args, "-adaptation_sets");
    args_add(&args, "%s", facts->has_audio ? "id=0,streams=v id=1,streams=a" : "id=0,streams=v");
    args_add(&args, "-hls_playlist");
    args_add(&args, "1");
    args_add(&args, "%s/manifest.mpd", directory);

    Run run;
    RunOutcome outcome = run_tool(&args, &run, job, 0);

    if (outcome != RUN_OK) {
        describe_failure("Encoding CMAF", outcome, &run, error, capacity);
    }

    run_free(&run);
    args_free(&args);

    return outcome == RUN_OK ? 0 : -1;
}

static int package_progressive(const Job *job, const SourceFacts *facts, const char *source, const char *directory,
                               const Output *output, char *error, size_t capacity) {
    Args args = { 0 };

    base_args(&args, facts->demuxer, "mov,matroska,avi,mpegts,mpeg", source);
    args_add(&args, "-map");
    args_add(&args, "0:v:%d", facts->video_ordinal);

    if (facts->has_audio) {
        args_add(&args, "-map");
        args_add(&args, "0:a:0");
    }

    args_add(&args, "-map_metadata");
    args_add(&args, "-1");
    args_add(&args, "-map_chapters");
    args_add(&args, "-1");
    args_add(&args, "-sn");
    args_add(&args, "-dn");
    args_add(&args, "-vf");
    args_add(&args, "scale=%d:%d", output->width, output->height);
    video_encoder_args(&args);
    args_add(&args, "-b:v");
    args_add(&args, "%dk", output->kbps);
    args_add(&args, "-maxrate");
    args_add(&args, "%dk", output->kbps * 107 / 100);
    args_add(&args, "-bufsize");
    args_add(&args, "%dk", output->kbps * 3 / 2);

    if (facts->has_audio) {
        audio_encoder_args(&args);
    }

    args_add(&args, "-movflags");
    args_add(&args, "+faststart");
    args_add(&args, "%s/progressive.mp4", directory);

    Run run;
    RunOutcome outcome = run_tool(&args, &run, job, 0);

    if (outcome != RUN_OK) {
        describe_failure("Encoding the progressive MP4", outcome, &run, error, capacity);
    }

    run_free(&run);
    args_free(&args);

    return outcome == RUN_OK ? 0 : -1;
}

static int convert_subtitle(const Job *job, const JobSubtitle *subtitle, const char *directory, SubtitleTrack *track, double duration) {
    char source[800];
    char target[1100];
    char playlist_path[1100];
    struct stat info;

    if (!media_file_name_plain(subtitle->file) ||
        (!media_has_extension(subtitle->file, ".vtt") && !media_has_extension(subtitle->file, ".srt"))) {
        say("skipping subtitle %s: not a plain .vtt or .srt name", subtitle->file);

        return -1;
    }

    snprintf(source, sizeof(source), "%s/%s", config.incoming, subtitle->file);

    if (stat(source, &info) != 0 || !S_ISREG(info.st_mode) || info.st_size > config.subtitle_max_bytes) {
        say("skipping subtitle %s: missing or larger than %ld bytes", subtitle->file, config.subtitle_max_bytes);

        return -1;
    }

    memset(track, 0, sizeof(*track));

    track->language_id = subtitle->language_id;

    snprintf(track->label, sizeof(track->label), "%s", subtitle->language);
    snprintf(track->tag, sizeof(track->tag), "%s", subtitle->tag);
    snprintf(track->vtt, sizeof(track->vtt), "subs/%d.vtt", subtitle->language_id);
    snprintf(track->playlist, sizeof(track->playlist), "subs/%d.m3u8", subtitle->language_id);
    if (join_path(target, sizeof(target), directory, track->vtt) != 0) {
        return -1;
    }

    Args args = { 0 };
    int is_srt = media_has_extension(subtitle->file, ".srt");

    base_args(&args, is_srt ? "srt" : "webvtt", "srt,webvtt", source);
    args_add(&args, "-map_metadata");
    args_add(&args, "-1");
    args_add(&args, "-f");
    args_add(&args, "webvtt");
    args_add(&args, "%s", target);

    Run run;
    RunOutcome outcome = run_tool(&args, &run, job, 0);

    run_free(&run);
    args_free(&args);

    size_t length = 0;
    char *converted = outcome == RUN_OK ? read_file(target, FILE_LIMIT, &length) : NULL;
    int valid = converted && length >= 6 && strncmp(converted, "WEBVTT", 6) == 0 && strstr(converted, "-->") != NULL;

    free(converted);

    if (!valid) {
        say("skipping subtitle %s: ffmpeg could not turn it into WebVTT", subtitle->file);
        unlink(target);

        return -1;
    }

    char name[32];

    snprintf(name, sizeof(name), "%d.vtt", subtitle->language_id);
    if (join_path(playlist_path, sizeof(playlist_path), directory, track->playlist) != 0) {
        return -1;
    }

    char *playlist = subtitle_playlist_render(name, duration);
    int written = playlist ? write_file(playlist_path, playlist) : -1;

    free(playlist);

    return written;
}

static int rewrite_playlist(const char *directory, const Representation *representation, MediaPlaylist *playlist,
                            char *path, size_t path_capacity, char *error, size_t capacity) {
    char source[1100];
    char target[1100];

    char ffmpeg_name[64];

    message(ffmpeg_name, sizeof(ffmpeg_name), "media_%s.m3u8", representation->id);

    if (join_path(source, sizeof(source), directory, ffmpeg_name) != 0) {
        message(error, capacity, "The package folder name is too long.");

        return -1;
    }

    char *text = read_file(source, FILE_LIMIT, NULL);

    if (!text) {
        snprintf(error, capacity, "ffmpeg wrote no HLS playlist for representation %s.", representation->id);

        return -1;
    }

    char parse_error[256];
    int parsed = playlist_parse(text, playlist, parse_error, sizeof(parse_error));

    free(text);

    if (parsed != 0) {
        message(error, capacity, "Representation %s: %s", representation->id, parse_error);

        return -1;
    }

    char file[1100];

    playlist->init_bytes = join_path(file, sizeof(file), directory, playlist->init) == 0 ? file_size(file) : 0;

    for (int i = 0; i < playlist->count; i++) {
        playlist->segments[i].bytes = join_path(file, sizeof(file), directory, playlist->segments[i].uri) == 0
            ? file_size(file)
            : 0;

        if (playlist->segments[i].bytes == 0) {
            snprintf(error, capacity, "Segment %s is missing.", playlist->segments[i].uri);

            return -1;
        }
    }

    message(path, path_capacity, "%s-%s.m3u8", representation->kind, representation->id);

    if (join_path(target, sizeof(target), directory, path) != 0) {
        playlist_free(playlist);
        message(error, capacity, "The package folder name is too long.");

        return -1;
    }

    char *rendered = playlist_render(playlist);
    int written = rendered ? write_file(target, rendered) : -1;

    free(rendered);
    unlink(source);

    if (written != 0) {
        snprintf(error, capacity, "Could not write %s.", path);

        return -1;
    }

    return 0;
}

static json_t *rendition_json(const char *kind, const char *codec, double bitrate, int width, int height, const char *language, const char *path) {
    json_t *item = json_object();
    long long rate = (long long)(bitrate + 0.5);

    json_object_set_new(item, "kind", json_string(kind));
    json_object_set_new(item, "codec", json_string(codec));
    json_object_set_new(item, "bitrate", json_integer(rate > 0 ? rate : 1));
    json_object_set_new(item, "width", width > 0 ? json_integer(width) : json_null());
    json_object_set_new(item, "height", height > 0 ? json_integer(height) : json_null());
    json_object_set_new(item, "language", language ? json_string(language) : json_null());
    json_object_set_new(item, "path", json_string(path));

    return item;
}

static int build_package(const Job *job, const SourceFacts *facts, const char *source, const char *directory,
                         const char *storage, char *error, size_t capacity) {
    Output outputs[LADDER_MAX];
    int output_count = ladder_plan(facts->width, facts->height, config.rungs, config.rung_count, outputs, LADDER_MAX);

    if (output_count <= 0) {
        snprintf(error, capacity, "No rendition fits a %dx%d source.", facts->width, facts->height);

        return -1;
    }

    say("title %d: %dx%d, %.1f s, %d rendition(s)%s", job->content_id, facts->width, facts->height, facts->duration,
        output_count, facts->has_audio ? " plus audio" : "");

    if (package_cmaf(job, facts, source, directory, outputs, output_count, error, capacity) != 0) {
        return -1;
    }

    char mpd_path[1100];

    char *mpd = join_path(mpd_path, sizeof(mpd_path), directory, "manifest.mpd") == 0
        ? read_file(mpd_path, FILE_LIMIT, NULL)
        : NULL;
    Representation representations[MPD_REPRESENTATIONS_MAX];
    int representation_count = mpd ? mpd_representations(mpd, representations, MPD_REPRESENTATIONS_MAX) : -1;

    if (representation_count <= 0) {
        free(mpd);
        snprintf(error, capacity, "ffmpeg wrote no usable DASH manifest.");

        return -1;
    }

    VideoVariant videos[MPD_REPRESENTATIONS_MAX];
    AudioTrack audio;
    int video_count = 0;
    int has_audio = 0;
    json_t *renditions = json_array();
    double duration = facts->duration;
    char video_codec[64] = "";
    int progressive_index = ladder_progressive(outputs, output_count, config.progressive_height);

    for (int i = 0; i < representation_count; i++) {
        Representation *representation = &representations[i];
        MediaPlaylist playlist;
        char path[64];
        double peak = 0;
        double average = 0;
        double length = 0;

        if (strcmp(representation->kind, "video") != 0 && strcmp(representation->kind, "audio") != 0) {
            continue;
        }

        if (!media_path_safe(representation->id) ||
            rewrite_playlist(directory, representation, &playlist, path, sizeof(path), error, capacity) != 0) {
            if (!error[0]) {
                snprintf(error, capacity, "ffmpeg wrote an unexpected representation id.");
            }

            free(mpd);
            json_decref(renditions);

            return -1;
        }

        playlist_rates(&playlist, &peak, &average, &length);
        playlist_free(&playlist);

        if (length > 0) {
            duration = length;
        }

        if (strcmp(representation->kind, "video") == 0) {
            VideoVariant *video = &videos[video_count++];

            memset(video, 0, sizeof(*video));
            snprintf(video->path, sizeof(video->path), "%s", path);
            snprintf(video->codecs, sizeof(video->codecs), "%s", representation->codecs);

            video->peak = peak;
            video->average = average;
            video->width = representation->width;
            video->height = representation->height;
            video->frame_rate = representation->frame_rate;

            if (progressive_index >= 0 && representation->width == outputs[progressive_index].width &&
                representation->height == outputs[progressive_index].height) {
                snprintf(video_codec, sizeof(video_codec), "%s", representation->codecs);
            }

            json_array_append_new(renditions, rendition_json("video", representation->codecs, average,
                                                             representation->width, representation->height, NULL, path));
        }
        else {
            has_audio = 1;

            memset(&audio, 0, sizeof(audio));
            snprintf(audio.path, sizeof(audio.path), "%s", path);
            snprintf(audio.codecs, sizeof(audio.codecs), "%s", representation->codecs);

            audio.peak = peak;
            audio.average = average;
            audio.channels = representation->channels;

            json_array_append_new(renditions, rendition_json("audio", representation->codecs, average, 0, 0, "und", path));
        }
    }

    if (video_count == 0) {
        free(mpd);
        json_decref(renditions);
        snprintf(error, capacity, "ffmpeg produced no video representation.");

        return -1;
    }

    SubtitleTrack subtitles[MAX_SUBTITLES];
    int subtitle_count = 0;
    json_t *subtitle_items = json_array();
    char subtitle_folder[1100];

    if (job->subtitle_count > 0) {
        if (join_path(subtitle_folder, sizeof(subtitle_folder), directory, "subs") == 0) {
            make_directory(subtitle_folder);
        }
    }

    for (int i = 0; i < job->subtitle_count; i++) {
        if (convert_subtitle(job, &job->subtitles[i], directory, &subtitles[subtitle_count], duration) == 0) {
            json_array_append_new(subtitle_items, json_pack("{s:i, s:s}", "language_id", subtitles[subtitle_count].language_id,
                                                            "uri", subtitles[subtitle_count].vtt));
            subtitle_count++;
        }
    }

    char ffmpeg_master[1100];

    if (join_path(ffmpeg_master, sizeof(ffmpeg_master), directory, "master.m3u8") == 0) {
        unlink(ffmpeg_master);
    }

    char *master = master_render(videos, video_count, has_audio ? &audio : NULL, subtitles, subtitle_count);
    char *patched = mpd_with_subtitles(mpd, subtitles, subtitle_count);

    free(mpd);

    if (!master || !patched || write_file(ffmpeg_master, master) != 0 || write_file(mpd_path, patched) != 0) {
        free(master);
        free(patched);
        json_decref(renditions);
        json_decref(subtitle_items);
        snprintf(error, capacity, "Could not write the master playlist or the DASH manifest.");

        return -1;
    }

    free(master);
    free(patched);

    if (progressive_index >= 0) {
        if (package_progressive(job, facts, source, directory, &outputs[progressive_index], error, capacity) != 0) {
            json_decref(renditions);
            json_decref(subtitle_items);

            return -1;
        }

        char progressive_path[1100];
        char codecs[160];

        if (join_path(progressive_path, sizeof(progressive_path), directory, "progressive.mp4") != 0) {
            progressive_path[0] = '\0';
        }
        snprintf(codecs, sizeof(codecs), "%s%s%s", video_codec[0] ? video_codec : videos[0].codecs,
                 has_audio ? "," : "", has_audio ? audio.codecs : "");

        double size = (double)file_size(progressive_path);

        if (size <= 0) {
            json_decref(renditions);
            json_decref(subtitle_items);
            snprintf(error, capacity, "ffmpeg wrote an empty progressive MP4.");

            return -1;
        }

        json_array_append_new(renditions, rendition_json("progressive", codecs, size * 8.0 / duration,
                                                         outputs[progressive_index].width, outputs[progressive_index].height,
                                                         NULL, "progressive.mp4"));
    }

    json_t *report = claim_reference(job);

    json_object_set_new(report, "storage_path", json_string(storage));
    json_object_set_new(report, "hls_path", json_string("master.m3u8"));
    json_object_set_new(report, "dash_path", json_string("manifest.mpd"));
    json_object_set_new(report, "duration_seconds", json_real(duration));
    json_object_set_new(report, "renditions", renditions);
    json_object_set_new(report, "subtitles", subtitle_items);

    ClientResponse *response = malloc(sizeof(*response));
    char path[128];
    int result = -1;

    snprintf(path, sizeof(path), "/internal/v1/packaging/%d/done", job->content_id);

    if (!response || api_post(path, report, response) != 0) {
        snprintf(error, capacity, "Could not report the finished package to the API.");
    }
    else if (response->status == 204 || response->status == 200) {
        result = 0;
    }
    else if (response->status == 409) {
        result = 1;
    }
    else {
        snprintf(error, capacity, "The API refused the package (status %d): %.300s", response->status, response->body);
    }

    json_decref(report);
    free(response);

    return result;
}

static void work(const Job *job) {
    char error[ERROR_MAX] = "";
    char source[800];
    char storage[MEDIA_PATH_MAX + 1];
    char directory[1100];
    struct stat info;
    unsigned char random_bytes[4] = { 0 };
    time_t now = time(NULL);
    struct tm utc;

    say("packaging title %d from %s (attempt %d)", job->content_id, job->source, job->attempts);

    remove_orphans(job);

    if (!media_file_name_plain(job->source)) {
        report_failure(job, "The source is not a plain file name in the incoming folder.");

        return;
    }

    snprintf(source, sizeof(source), "%s/%s", config.incoming, job->source);

    if (stat(source, &info) != 0 || !S_ISREG(info.st_mode)) {
        report_failure(job, "The source file is no longer in the incoming folder.");

        return;
    }

    Args probe = { 0 };

    args_add(&probe, "%s", config.ffprobe);
    args_add(&probe, "-v");
    args_add(&probe, "error");
    args_add(&probe, "-protocol_whitelist");
    args_add(&probe, "file");
    args_add(&probe, "-format_whitelist");
    args_add(&probe, "mov,matroska,avi,mpegts,mpeg");
    args_add(&probe, "-print_format");
    args_add(&probe, "json");
    args_add(&probe, "-show_format");
    args_add(&probe, "-show_streams");
    args_add(&probe, "%s", source);

    Run run;
    RunOutcome outcome = run_tool(&probe, &run, job, 1);
    SourceFacts facts;

    args_free(&probe);

    if (outcome == RUN_STOPPED) {
        run_free(&run);
        release(job);

        return;
    }

    if (outcome != RUN_OK) {
        describe_failure("Probing the source", outcome, &run, error, sizeof(error));
        run_free(&run);

        if (outcome != RUN_LOST) {
            report_failure(job, error);
        }

        return;
    }

    int parsed = probe_parse(run.out ? run.out : "", run.out_length, &facts, error, sizeof(error));

    run_free(&run);

    if (parsed != 0) {
        report_failure(job, error);

        return;
    }

    if (getrandom(random_bytes, sizeof(random_bytes), 0) != (ssize_t)sizeof(random_bytes)) {
        random_bytes[0] = (unsigned char)(now_ms() & 0xff);
    }

    gmtime_r(&now, &utc);
    snprintf(storage, sizeof(storage), "%d/%04d%02d%02d%02d%02d%02d-%02x%02x%02x%02x", job->content_id,
             utc.tm_year + 1900, utc.tm_mon + 1, utc.tm_mday, utc.tm_hour, utc.tm_min, utc.tm_sec,
             random_bytes[0], random_bytes[1], random_bytes[2], random_bytes[3]);
    snprintf(directory, sizeof(directory), "%s/%s", config.library, storage);

    if (make_directory(directory) != 0) {
        snprintf(error, sizeof(error), "Could not create %s: %s", storage, strerror(errno));
        report_failure(job, error);

        return;
    }

    int64_t started = now_ms();
    int result = build_package(job, &facts, source, directory, storage, error, sizeof(error));

    if (result == 0) {
        say("title %d is ready in %s after %.1f s", job->content_id, storage, (double)(now_ms() - started) / 1000.0);

        if (job->storage[0] && strcmp(job->storage, storage) != 0) {
            char previous[1100];

            snprintf(previous, sizeof(previous), "%s/%s", config.library, job->storage);
            remove_tree(previous);
        }

        return;
    }

    remove_tree(directory);

    if (!running) {
        release(job);

        return;
    }

    if (result == 1 || strstr(error, "was taken away")) {
        say("title %d was taken away before it finished; its files were removed", job->content_id);

        return;
    }

    report_failure(job, error[0] ? error : "Packaging failed.");
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

    struct sigaction action;

    memset(&action, 0, sizeof(action));
    action.sa_handler = on_signal;
    sigaction(SIGTERM, &action, NULL);
    sigaction(SIGINT, &action, NULL);
    signal(SIGPIPE, SIG_IGN);

    if (make_directory(config.library) != 0) {
        say("cannot use the media library %s: %s", config.library, strerror(errno));

        return 1;
    }

    say("watching %s:%d for work; sources in %s, packages in %s", config.api_host, config.api_port, config.incoming, config.library);

    int quiet_failures = 0;

    while (running) {
        Job job;

        touch_heartbeat();

        int claimed = claim(&job);

        if (claimed > 0) {
            quiet_failures = 0;
            work(&job);

            continue;
        }

        if (claimed < 0 && quiet_failures++ % 12 == 0) {
            say("the API at %s:%d is not handing out work yet", config.api_host, config.api_port);
        }

        for (int waited = 0; waited < config.poll_seconds * 10 && running; waited++) {
            usleep(100000);
        }
    }

    say("stopped");

    return 0;
}
