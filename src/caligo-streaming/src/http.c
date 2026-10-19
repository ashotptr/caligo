#define _GNU_SOURCE

#include "http.h"

#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>

int http_request_complete(const char *buffer, size_t length) {
    return length >= 4 && memmem(buffer, length, "\r\n\r\n", 4) != NULL;
}

int http_header_value(const char *headers, const char *name, char *value, size_t capacity) {
    size_t name_length = strlen(name);
    const char *line = headers;

    while (line && *line) {
        const char *end = strstr(line, "\r\n");
        size_t line_length = end ? (size_t)(end - line) : strlen(line);

        if (line_length > name_length && line[name_length] == ':' && strncasecmp(line, name, name_length) == 0) {
            const char *start = line + name_length + 1;
            const char *stop = line + line_length;

            while (start < stop && (*start == ' ' || *start == '\t')) {
                start++;
            }

            while (stop > start && (stop[-1] == ' ' || stop[-1] == '\t')) {
                stop--;
            }

            size_t value_length = (size_t)(stop - start);

            if (value_length >= capacity) {
                value_length = capacity - 1;
            }

            memcpy(value, start, value_length);

            value[value_length] = '\0';

            return 1;
        }

        line = end ? end + 2 : NULL;
    }

    return 0;
}

static int parse_range(const char *value, uint64_t *start) {
    if (strncasecmp(value, "bytes=", 6) != 0) {
        return 0;
    }

    const char *digits = value + 6;
    uint64_t number = 0;
    int count = 0;

    while (isdigit((unsigned char)*digits)) {
        if (count++ >= 18) {
            return 0;
        }

        number = number * 10 + (uint64_t)(*digits - '0');
        digits++;
    }

    if (count == 0 || *digits != '-') {
        return 0;
    }

    *start = number;

    return 1;
}

int http_parse_request(const char *buffer, size_t length, HttpRequest *request) {
    memset(request, 0, sizeof(*request));

    const char *line_end = memmem(buffer, length, "\r\n", 2);

    if (!line_end) {
        return -1;
    }

    const char *space = memchr(buffer, ' ', (size_t)(line_end - buffer));

    if (!space || space == buffer || (size_t)(space - buffer) >= sizeof(request->method)) {
        return -1;
    }

    memcpy(request->method, buffer, (size_t)(space - buffer));

    const char *target = space + 1;
    const char *target_end = memchr(target, ' ', (size_t)(line_end - target));

    if (!target_end || target_end == target || (size_t)(target_end - target) >= HTTP_TARGET_MAX || *target != '/') {
        return -1;
    }

    if ((size_t)(line_end - target_end) != 9 || memcmp(target_end + 1, "HTTP/1.", 7) != 0) {
        return -1;
    }

    request->version_minor = target_end[8] == '0' ? 0 : 1;

    memcpy(request->target, target, (size_t)(target_end - target));

    const char *query = strchr(request->target, '?');
    size_t path_length = query ? (size_t)(query - request->target) : strlen(request->target);

    memcpy(request->path, request->target, path_length);

    request->has_query = query != NULL && query[1] != '\0';

    const char *headers = line_end + 2;
    char value[128];

    if (http_header_value(headers, "Range", value, sizeof(value)) && parse_range(value, &request->range_start)) {
        request->has_range = 1;
    }

    if (http_header_value(headers, "Icy-MetaData", value, sizeof(value)) && strcmp(value, "1") == 0) {
        request->wants_icy = 1;
    }

    if (http_header_value(headers, "Accept", value, sizeof(value)) && strcasestr(value, "text/event-stream")) {
        request->wants_events = 1;
    }

    return 0;
}

void chunk_decoder_init(ChunkDecoder *decoder) {
    decoder->state = CHUNK_SIZE;
    decoder->remaining = 0;
    decoder->digits = 0;
}

static int hex_value(unsigned char byte) {
    if (byte >= '0' && byte <= '9') {
        return byte - '0';
    }

    if (byte >= 'a' && byte <= 'f') {
        return byte - 'a' + 10;
    }

    if (byte >= 'A' && byte <= 'F') {
        return byte - 'A' + 10;
    }

    return -1;
}

int chunk_decoder_feed(ChunkDecoder *decoder, const unsigned char *data, size_t length, ChunkDataFn emit, void *context) {
    size_t position = 0;

    while (position < length) {
        unsigned char byte = data[position];

        switch (decoder->state) {
            case CHUNK_SIZE: {
                int digit = hex_value(byte);

                if (digit >= 0) {
                    if (decoder->digits++ >= 15) {
                        decoder->state = CHUNK_ERROR;

                        return -1;
                    }

                    decoder->remaining = decoder->remaining * 16 + (uint64_t)digit;
                    position++;
                }
                else if (decoder->digits == 0) {
                    decoder->state = CHUNK_ERROR;

                    return -1;
                }
                else if (byte == '\r') {
                    decoder->state = CHUNK_SIZE_LF;
                    position++;
                }
                else if (byte == ';' || byte == ' ' || byte == '\t') {
                    decoder->state = CHUNK_EXTENSION;
                    position++;
                }
                else {
                    decoder->state = CHUNK_ERROR;

                    return -1;
                }

                break;
            }

            case CHUNK_EXTENSION:
                if (byte == '\r') {
                    decoder->state = CHUNK_SIZE_LF;
                }

                position++;

                break;

            case CHUNK_SIZE_LF:
                if (byte != '\n') {
                    decoder->state = CHUNK_ERROR;

                    return -1;
                }

                decoder->state = decoder->remaining == 0 ? CHUNK_DONE : CHUNK_DATA;
                position++;

                break;

            case CHUNK_DATA: {
                size_t available = length - position;
                size_t take = decoder->remaining < available ? (size_t)decoder->remaining : available;

                emit(context, data + position, take);

                decoder->remaining -= take;
                position += take;

                if (decoder->remaining == 0) {
                    decoder->state = CHUNK_DATA_CR;
                }

                break;
            }

            case CHUNK_DATA_CR:
                if (byte != '\r') {
                    decoder->state = CHUNK_ERROR;

                    return -1;
                }

                decoder->state = CHUNK_DATA_LF;
                position++;

                break;

            case CHUNK_DATA_LF:
                if (byte != '\n') {
                    decoder->state = CHUNK_ERROR;

                    return -1;
                }

                decoder->state = CHUNK_SIZE;
                decoder->digits = 0;
                decoder->remaining = 0;
                position++;

                break;

            case CHUNK_DONE:
                return 1;

            case CHUNK_ERROR:
                return -1;
        }
    }

    return decoder->state == CHUNK_DONE ? 1 : 0;
}

static const char *after(const char *text, const char *marker) {
    const char *found = text ? strstr(text, marker) : NULL;

    return found ? found + strlen(marker) : NULL;
}

static void copy_between(char *target, size_t capacity, const char *start, const char *end) {
    size_t length = start && end && end > start ? (size_t)(end - start) : 0;

    if (length >= capacity) {
        length = capacity - 1;

        while (length > 0 && ((unsigned char)start[length] & 0xC0) == 0x80) {
            length--;
        }
    }

    if (length > 0) {
        memcpy(target, start, length);
    }

    target[length] = '\0';
}

int radio_event_parse(const char *data, RadioEvent *event) {
    memset(event, 0, sizeof(*event));

    const char *playing = after(data, "\"playing\": \"");
    const char *artist_key = playing ? strstr(playing, "\", \"artist\": \"") : NULL;
    const char *artist = after(artist_key, "\", \"artist\": \"");
    const char *album_key = artist ? strstr(artist, "\", \"album\": \"") : NULL;
    const char *album = after(album_key, "\", \"album\": \"");
    const char *title_key = album ? strstr(album, "\", \"title\": \"") : NULL;
    const char *title = after(title_key, "\", \"title\": \"");
    const char *title_end = NULL;

    if (title) {
        const char *search = title;
        const char *found;

        while ((found = strstr(search, "\", \"bitrate\"")) != NULL) {
            title_end = found;
            search = found + 1;
        }
    }

    if (!title_end || title_end == title) {
        return 0;
    }

    copy_between(event->playing, sizeof(event->playing), playing, artist_key);
    copy_between(event->artist, sizeof(event->artist), artist, album_key);
    copy_between(event->album, sizeof(event->album), album, title_key);
    copy_between(event->title, sizeof(event->title), title, title_end);

    event->is_request = strstr(title_end, "\"is_request\": true") != NULL;

    return 1;
}

size_t json_escape(char *target, size_t capacity, const char *source) {
    size_t written = 0;

    if (capacity == 0) {
        return 0;
    }

    for (const unsigned char *cursor = (const unsigned char *)source; *cursor; cursor++) {
        char escape[8];
        size_t length;

        if (*cursor == '"' || *cursor == '\\') {
            escape[0] = '\\';
            escape[1] = (char)*cursor;
            length = 2;
        }
        else if (*cursor < 0x20) {
            length = (size_t)snprintf(escape, sizeof(escape), "\\u%04x", *cursor);
        }
        else {
            escape[0] = (char)*cursor;
            length = 1;
        }

        if (written + length >= capacity) {
            break;
        }

        memcpy(target + written, escape, length);

        written += length;
    }

    target[written] = '\0';

    return written;
}

uint64_t hash_text(const char *text) {
    uint64_t hash = 1469598103934665603ULL;

    for (const unsigned char *cursor = (const unsigned char *)text; *cursor; cursor++) {
        hash ^= *cursor;
        hash *= 1099511628211ULL;
    }

    return hash ? hash : 1;
}

static int read_number(const char **cursor, uint64_t *number) {
    const char *digits = *cursor;
    uint64_t value = 0;
    int count = 0;

    while (isdigit((unsigned char)*digits)) {
        if (count++ >= 18) {
            return 0;
        }

        value = value * 10 + (uint64_t)(*digits - '0');
        digits++;
    }

    if (count == 0) {
        return 0;
    }

    *cursor = digits;
    *number = value;

    return 1;
}

RangeResult http_byte_range(const char *value, uint64_t size, uint64_t *start, uint64_t *length) {
    if (strncasecmp(value, "bytes=", 6) != 0 || strchr(value, ',') != NULL) {
        return RANGE_NONE;
    }

    const char *cursor = value + 6;
    uint64_t first = 0;
    uint64_t last = 0;

    if (*cursor == '-') {
        cursor++;

        if (!read_number(&cursor, &last) || *cursor != '\0') {
            return RANGE_NONE;
        }

        if (last == 0 || size == 0) {
            return RANGE_UNSATISFIABLE;
        }

        *start = last >= size ? 0 : size - last;
        *length = size - *start;

        return RANGE_PARTIAL;
    }

    if (!read_number(&cursor, &first) || *cursor != '-') {
        return RANGE_NONE;
    }

    cursor++;

    if (*cursor == '\0') {
        if (first >= size) {
            return RANGE_UNSATISFIABLE;
        }

        *start = first;
        *length = size - first;

        return RANGE_PARTIAL;
    }

    if (!read_number(&cursor, &last) || *cursor != '\0' || last < first) {
        return RANGE_NONE;
    }

    if (first >= size) {
        return RANGE_UNSATISFIABLE;
    }

    if (last >= size) {
        last = size - 1;
    }

    *start = first;
    *length = last - first + 1;

    return RANGE_PARTIAL;
}

static int base64_value(unsigned char byte) {
    if (byte >= 'A' && byte <= 'Z') {
        return byte - 'A';
    }

    if (byte >= 'a' && byte <= 'z') {
        return byte - 'a' + 26;
    }

    if (byte >= '0' && byte <= '9') {
        return byte - '0' + 52;
    }

    if (byte == '+') {
        return 62;
    }

    return byte == '/' ? 63 : -1;
}

static int base64_decode(const char *text, unsigned char *out, size_t capacity, size_t *length) {
    uint32_t bits = 0;
    int count = 0;
    size_t written = 0;

    for (; *text && *text != '='; text++) {
        int value = base64_value((unsigned char)*text);

        if (value < 0) {
            return 0;
        }

        bits = bits << 6 | (uint32_t)value;
        count += 6;

        if (count >= 8) {
            count -= 8;

            if (written >= capacity) {
                return 0;
            }

            out[written++] = (unsigned char)(bits >> count);
        }
    }

    while (*text == '=') {
        text++;
    }

    if (*text != '\0') {
        return 0;
    }

    *length = written;

    return 1;
}

int http_basic_credentials(const char *value, char *user, size_t user_capacity, char *password, size_t password_capacity) {
    unsigned char decoded[512];
    size_t length = 0;

    if (strncasecmp(value, "Basic ", 6) != 0) {
        return 0;
    }

    value += 6;

    while (*value == ' ') {
        value++;
    }

    if (!base64_decode(value, decoded, sizeof(decoded), &length) || memchr(decoded, '\0', length) != NULL) {
        return 0;
    }

    const unsigned char *colon = memchr(decoded, ':', length);

    if (!colon) {
        return 0;
    }

    size_t user_length = (size_t)(colon - decoded);
    size_t password_length = length - user_length - 1;

    if (user_length >= user_capacity || password_length >= password_capacity) {
        return 0;
    }

    memcpy(user, decoded, user_length);
    memcpy(password, colon + 1, password_length);

    user[user_length] = '\0';
    password[password_length] = '\0';

    return 1;
}

int http_query_value(const char *target, const char *name, char *value, size_t capacity) {
    const char *cursor = strchr(target, '?');
    size_t name_length = strlen(name);

    if (!cursor || capacity == 0) {
        return 0;
    }

    cursor++;

    while (*cursor) {
        const char *end = strchr(cursor, '&');
        const char *stop = end ? end : cursor + strlen(cursor);
        const char *equals = memchr(cursor, '=', (size_t)(stop - cursor));
        const char *key_end = equals ? equals : stop;

        if ((size_t)(key_end - cursor) == name_length && strncmp(cursor, name, name_length) == 0) {
            size_t written = 0;
            const char *source = equals ? equals + 1 : stop;

            while (source < stop && written + 1 < capacity) {
                int high;
                int low;

                if (*source == '+') {
                    value[written++] = ' ';
                    source++;
                }
                else if (*source == '%' && stop - source >= 3 &&
                         (high = hex_value((unsigned char)source[1])) >= 0 &&
                         (low = hex_value((unsigned char)source[2])) >= 0) {
                    value[written++] = (char)(high * 16 + low);
                    source += 3;
                }
                else {
                    value[written++] = *source++;
                }
            }

            value[written] = '\0';

            return 1;
        }

        cursor = end ? end + 1 : stop;
    }

    return 0;
}

static int utf8_valid(const unsigned char *text) {
    while (*text) {
        int extra;
        uint32_t code;
        uint32_t minimum;

        if (*text < 0x80) {
            text++;

            continue;
        }

        if ((*text & 0xE0) == 0xC0) {
            extra = 1;
            code = *text & 0x1Fu;
            minimum = 0x80;
        }
        else if ((*text & 0xF0) == 0xE0) {
            extra = 2;
            code = *text & 0x0Fu;
            minimum = 0x800;
        }
        else if ((*text & 0xF8) == 0xF0) {
            extra = 3;
            code = *text & 0x07u;
            minimum = 0x10000;
        }
        else {
            return 0;
        }

        for (int index = 1; index <= extra; index++) {
            if ((text[index] & 0xC0) != 0x80) {
                return 0;
            }

            code = code << 6 | (text[index] & 0x3Fu);
        }

        if (code < minimum || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) {
            return 0;
        }

        text += extra + 1;
    }

    return 1;
}

void text_utf8_clean(const char *source, char *target, size_t capacity) {
    const unsigned char *cursor = (const unsigned char *)source;
    int valid = utf8_valid(cursor);
    size_t written = 0;

    if (capacity == 0) {
        return;
    }

    while (*cursor) {
        unsigned char encoded[4];
        size_t length = 1;

        if (*cursor < 0x20 || *cursor == 0x7F) {
            encoded[0] = ' ';
        }
        else if (*cursor < 0x80) {
            encoded[0] = *cursor;
        }
        else if (!valid) {
            encoded[0] = (unsigned char)(0xC0 | (*cursor >> 6));
            encoded[1] = (unsigned char)(0x80 | (*cursor & 0x3F));
            length = 2;
        }
        else {
            length = (*cursor & 0xE0) == 0xC0 ? 2 : (*cursor & 0xF0) == 0xE0 ? 3 : 4;

            memcpy(encoded, cursor, length);
        }

        if (written + length >= capacity) {
            break;
        }

        memcpy(target + written, encoded, length);

        written += length;
        cursor += valid && *cursor >= 0x80 ? length : 1;
    }

    target[written] = '\0';
}

int text_equal_secret(const char *given, const char *expected) {
    size_t given_length = strlen(given);
    size_t expected_length = strlen(expected);
    unsigned char difference = (unsigned char)(given_length != expected_length);

    for (size_t index = 0; index < expected_length; index++) {
        unsigned char left = index < given_length ? (unsigned char)given[index] : 0;

        difference = (unsigned char)(difference | (left ^ (unsigned char)expected[index]));
    }

    return difference == 0 && expected_length > 0;
}
