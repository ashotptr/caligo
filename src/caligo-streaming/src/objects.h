#ifndef RELAY_OBJECTS_H
#define RELAY_OBJECTS_H

#include <stddef.h>
#include <stdint.h>

#define VARINT_MAX ((1ULL << 62) - 1)

typedef struct {
    uint64_t group;
    uint32_t index;
    uint64_t timestamp;
    uint64_t offset;
    size_t length;
} ObjectEntry;

typedef struct {
    unsigned char *data;
    size_t capacity;
    uint64_t head;
    ObjectEntry *entries;
    size_t entry_capacity;
    uint64_t count;
    uint64_t group;
    uint32_t next_index;
    uint64_t group_first;
    uint64_t group_timestamp;
    uint64_t group_span;
    uint64_t floor;
    int started;
} ObjectTrack;

size_t varint_size(uint64_t value);

size_t varint_encode(uint64_t value, unsigned char *out);

long varint_decode(const unsigned char *data, size_t length, uint64_t *value);

int object_track_init(ObjectTrack *track, size_t data_capacity, size_t entry_capacity, uint64_t first_group, uint64_t group_span);

void object_track_free(ObjectTrack *track);

uint64_t object_track_append(ObjectTrack *track, const unsigned char *payload, size_t length, uint64_t timestamp, int new_group);

uint64_t object_track_oldest(const ObjectTrack *track);

const ObjectEntry *object_track_entry(const ObjectTrack *track, uint64_t sequence);

size_t object_track_copy(const ObjectTrack *track, const ObjectEntry *entry, unsigned char *out, size_t capacity);

void object_track_clear(ObjectTrack *track);

#endif
