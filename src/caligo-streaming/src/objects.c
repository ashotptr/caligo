#include "objects.h"

#include <stdlib.h>
#include <string.h>

size_t varint_size(uint64_t value) {
    if (value < 64) {
        return 1;
    }

    if (value < 16384) {
        return 2;
    }

    return value < 1073741824 ? 4 : 8;
}

size_t varint_encode(uint64_t value, unsigned char *out) {
    size_t size = varint_size(value);
    unsigned char prefix = size == 1 ? 0x00 : size == 2 ? 0x40 : size == 4 ? 0x80 : 0xC0;

    for (size_t index = 0; index < size; index++) {
        out[index] = (unsigned char)(value >> (8 * (size - 1 - index)));
    }

    out[0] = (unsigned char)((out[0] & 0x3F) | prefix);

    return size;
}

long varint_decode(const unsigned char *data, size_t length, uint64_t *value) {
    if (length < 1) {
        return 0;
    }

    size_t size = (size_t)1 << (data[0] >> 6);

    if (length < size) {
        return 0;
    }

    uint64_t result = data[0] & 0x3F;

    for (size_t index = 1; index < size; index++) {
        result = result << 8 | data[index];
    }

    *value = result;

    return (long)size;
}

int object_track_init(ObjectTrack *track, size_t data_capacity, size_t entry_capacity, uint64_t first_group, uint64_t group_span) {
    memset(track, 0, sizeof(*track));

    track->data = malloc(data_capacity);
    track->entries = calloc(entry_capacity, sizeof(ObjectEntry));
    track->capacity = data_capacity;
    track->entry_capacity = entry_capacity;
    track->group = first_group;
    track->group_span = group_span;

    if (!track->data || !track->entries || data_capacity == 0 || entry_capacity == 0) {
        object_track_free(track);

        return -1;
    }

    return 0;
}

void object_track_free(ObjectTrack *track) {
    free(track->data);
    free(track->entries);

    track->data = NULL;
    track->entries = NULL;
}

uint64_t object_track_append(ObjectTrack *track, const unsigned char *payload, size_t length, uint64_t timestamp, int new_group) {
    if (length > track->capacity) {
        length = 0;
    }

    if (!track->started || new_group || (track->group_span > 0 && timestamp - track->group_timestamp >= track->group_span) ||
        timestamp < track->group_timestamp) {
        if (track->started) {
            track->group++;
        }

        track->started = 1;
        track->next_index = 0;
        track->group_first = track->count;
        track->group_timestamp = timestamp;
    }

    uint64_t sequence = track->count;
    ObjectEntry *entry = &track->entries[sequence % track->entry_capacity];

    entry->group = track->group;
    entry->index = track->next_index++;
    entry->timestamp = timestamp;
    entry->offset = track->head;
    entry->length = length;

    size_t start = (size_t)(track->head % track->capacity);
    size_t first = track->capacity - start < length ? track->capacity - start : length;

    memcpy(track->data + start, payload, first);
    memcpy(track->data, payload + first, length - first);

    track->head += length;
    track->count++;

    return sequence;
}

static int readable(const ObjectTrack *track, uint64_t sequence) {
    const ObjectEntry *entry = &track->entries[sequence % track->entry_capacity];

    return track->head - entry->offset <= track->capacity;
}

uint64_t object_track_oldest(const ObjectTrack *track) {
    uint64_t low = track->count > track->entry_capacity ? track->count - track->entry_capacity : 0;
    uint64_t high = track->count;

    if (low < track->floor) {
        low = track->floor;
    }

    while (low < high) {
        uint64_t middle = low + (high - low) / 2;

        if (readable(track, middle)) {
            high = middle;
        }
        else {
            low = middle + 1;
        }
    }

    return low;
}

const ObjectEntry *object_track_entry(const ObjectTrack *track, uint64_t sequence) {
    if (sequence >= track->count || sequence < object_track_oldest(track)) {
        return NULL;
    }

    return &track->entries[sequence % track->entry_capacity];
}

size_t object_track_copy(const ObjectTrack *track, const ObjectEntry *entry, unsigned char *out, size_t capacity) {
    if (entry->length > capacity || track->head - entry->offset > track->capacity) {
        return 0;
    }

    size_t start = (size_t)(entry->offset % track->capacity);
    size_t first = track->capacity - start < entry->length ? track->capacity - start : entry->length;

    memcpy(out, track->data + start, first);
    memcpy(out + first, track->data, entry->length - first);

    return entry->length;
}

void object_track_clear(ObjectTrack *track) {
    track->floor = track->count;
    track->started = 0;
    track->group++;
}
