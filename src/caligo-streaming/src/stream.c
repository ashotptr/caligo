#include "stream.h"

#include <stdlib.h>
#include <string.h>

#define MIN_FRAME_BYTES 48

int stream_init(Stream *stream, size_t capacity) {
    memset(stream, 0, sizeof(*stream));

    if (capacity < 65536 || (capacity & (capacity - 1)) != 0) {
        return -1;
    }

    stream->capacity = capacity;
    stream->mark_capacity = capacity / MIN_FRAME_BYTES;
    stream->data = malloc(capacity);
    stream->marks = calloc(stream->mark_capacity, sizeof(StreamMark));

    if (!stream->data || !stream->marks) {
        stream_free(stream);

        return -1;
    }

    return 0;
}

void stream_free(Stream *stream) {
    free(stream->data);
    free(stream->marks);

    stream->data = NULL;
    stream->marks = NULL;
}

void stream_append(Stream *stream, const unsigned char *frame, size_t length, uint64_t duration_ns) {
    if (length == 0 || length > stream->capacity) {
        return;
    }

    StreamMark *mark = &stream->marks[stream->mark_count % stream->mark_capacity];

    mark->position = stream->head;
    mark->time_ns = stream->time_ns;

    stream->mark_count++;

    size_t offset = (size_t)(stream->head & (stream->capacity - 1));
    size_t first = stream->capacity - offset;

    if (first > length) {
        first = length;
    }

    memcpy(stream->data + offset, frame, first);
    memcpy(stream->data, frame + first, length - first);

    stream->head += length;
    stream->time_ns += duration_ns;
}

void stream_clear(Stream *stream) {
    stream->mark_count = 0;
}

static uint64_t oldest_mark_index(const Stream *stream) {
    return stream->mark_count > stream->mark_capacity ? stream->mark_count - stream->mark_capacity : 0;
}

uint64_t stream_tail(const Stream *stream) {
    uint64_t by_bytes = stream->head > stream->capacity ? stream->head - stream->capacity : 0;

    if (stream->mark_count == 0) {
        return stream->head;
    }

    uint64_t index = oldest_mark_index(stream);
    uint64_t by_marks = stream->marks[index % stream->mark_capacity].position;

    return by_bytes > by_marks ? by_bytes : by_marks;
}

int stream_contains(const Stream *stream, uint64_t position) {
    return position >= stream_tail(stream) && position <= stream->head;
}

int stream_frame_before(const Stream *stream, uint64_t position, StreamMark *mark) {
    if (stream->mark_count == 0) {
        return 0;
    }

    uint64_t low = oldest_mark_index(stream);
    uint64_t high = stream->mark_count;
    uint64_t tail = stream_tail(stream);

    while (low < high) {
        uint64_t middle = low + (high - low) / 2;

        if (stream->marks[middle % stream->mark_capacity].position < tail) {
            low = middle + 1;
        }
        else {
            high = middle;
        }
    }

    uint64_t first = low;

    if (first >= stream->mark_count) {
        return 0;
    }

    low = first;
    high = stream->mark_count;

    while (high - low > 1) {
        uint64_t middle = low + (high - low) / 2;

        if (stream->marks[middle % stream->mark_capacity].position <= position) {
            low = middle;
        }
        else {
            high = middle;
        }
    }

    *mark = stream->marks[low % stream->mark_capacity];

    return 1;
}

uint64_t stream_start_position(const Stream *stream, size_t burst_bytes) {
    StreamMark mark;
    uint64_t wanted = stream->head > burst_bytes ? stream->head - burst_bytes : 0;

    if (!stream_frame_before(stream, wanted, &mark)) {
        return stream->head;
    }

    return mark.position;
}

size_t stream_read(const Stream *stream, uint64_t position, const unsigned char **bytes) {
    if (position >= stream->head || position < stream_tail(stream)) {
        *bytes = NULL;

        return 0;
    }

    size_t offset = (size_t)(position & (stream->capacity - 1));
    uint64_t available = stream->head - position;
    size_t contiguous = stream->capacity - offset;

    *bytes = stream->data + offset;

    return available < contiguous ? (size_t)available : contiguous;
}

static void copy_text(char *target, const char *source) {
    size_t length = source ? strlen(source) : 0;

    if (length >= STREAM_TEXT) {
        length = STREAM_TEXT - 1;

        while (length > 0 && ((unsigned char)source[length] & 0xC0) == 0x80) {
            length--;
        }
    }

    memcpy(target, source ? source : "", length);

    target[length] = '\0';
}

void stream_set_title(Stream *stream, const char *artist, const char *album, const char *title, int is_request) {
    StreamTitle *entry = &stream->titles[stream->title_count % STREAM_TITLES];

    entry->id = stream->title_id_base + stream->title_count + 1;
    entry->position = stream->head;
    entry->is_request = is_request ? 1 : 0;

    copy_text(entry->artist, artist);
    copy_text(entry->album, album);
    copy_text(entry->title, title);

    stream->title_count++;
}

const StreamTitle *stream_title_at(const Stream *stream, uint64_t position) {
    if (stream->title_count == 0) {
        return NULL;
    }

    uint64_t oldest = stream->title_count > STREAM_TITLES ? stream->title_count - STREAM_TITLES : 0;
    const StreamTitle *found = &stream->titles[oldest % STREAM_TITLES];

    for (uint64_t index = oldest; index < stream->title_count; index++) {
        const StreamTitle *entry = &stream->titles[index % STREAM_TITLES];

        if (entry->position <= position) {
            found = entry;
        }
        else {
            break;
        }
    }

    return found;
}
