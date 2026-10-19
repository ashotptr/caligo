#ifndef CALIGO_PROBE_H
#define CALIGO_PROBE_H

#include <stddef.h>

typedef struct {
    char demuxer[32];
    double duration;
    int has_video;
    int video_ordinal;
    int width;
    int height;
    int rotation;
    double frame_rate;
    int has_audio;
    int channels;
} SourceFacts;

int probe_parse(const char *json, size_t length, SourceFacts *facts, char *error, size_t error_capacity);

const char *probe_demuxer(const char *format_name);

#endif
