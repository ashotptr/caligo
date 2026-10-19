#include "probe.h"

#include <jansson.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char *const allowed[] = { "mov", "matroska", "avi", "mpegts", "mpeg" };

const char *probe_demuxer(const char *format_name) {
    if (!format_name) {
        return NULL;
    }

    char first[64];
    const char *comma = strchr(format_name, ',');
    size_t length = comma ? (size_t)(comma - format_name) : strlen(format_name);

    if (length == 0 || length >= sizeof(first)) {
        return NULL;
    }

    memcpy(first, format_name, length);
    first[length] = '\0';

    for (size_t i = 0; i < sizeof(allowed) / sizeof(allowed[0]); i++) {
        if (strcmp(first, allowed[i]) == 0) {
            return allowed[i];
        }
    }

    return NULL;
}

static double number_of(json_t *value) {
    if (json_is_number(value)) {
        return json_number_value(value);
    }

    if (json_is_string(value)) {
        char *end = NULL;
        double parsed = strtod(json_string_value(value), &end);

        return end && end != json_string_value(value) ? parsed : 0;
    }

    return 0;
}

static double rate_of(json_t *value) {
    const char *text = json_is_string(value) ? json_string_value(value) : NULL;

    if (!text) {
        return 0;
    }

    char *end = NULL;
    double numerator = strtod(text, &end);

    if (!end || end == text) {
        return 0;
    }

    if (*end != '/') {
        return numerator;
    }

    double denominator = strtod(end + 1, NULL);

    return denominator > 0 ? numerator / denominator : 0;
}

static int rotation_of(json_t *stream) {
    json_t *side_data = json_object_get(stream, "side_data_list");
    size_t index;
    json_t *item;

    json_array_foreach(side_data, index, item) {
        json_t *rotation = json_object_get(item, "rotation");

        if (rotation) {
            return (int)number_of(rotation);
        }
    }

    json_t *tags = json_object_get(stream, "tags");

    return (int)number_of(json_object_get(tags, "rotate"));
}

int probe_parse(const char *json, size_t length, SourceFacts *facts, char *error, size_t error_capacity) {
    json_error_t parse_error;
    json_t *root = json_loadb(json, length, 0, &parse_error);

    memset(facts, 0, sizeof(*facts));

    if (!root) {
        snprintf(error, error_capacity, "ffprobe did not return JSON: %s", parse_error.text);

        return -1;
    }

    json_t *format = json_object_get(root, "format");
    const char *format_name = json_string_value(json_object_get(format, "format_name"));
    const char *demuxer = probe_demuxer(format_name);

    if (!demuxer) {
        snprintf(error, error_capacity, "The source is a %s file; only MP4/MOV, Matroska/WebM, AVI and MPEG files are packaged.",
                 format_name ? format_name : "unknown");
        json_decref(root);

        return -1;
    }

    snprintf(facts->demuxer, sizeof(facts->demuxer), "%s", demuxer);

    facts->duration = number_of(json_object_get(format, "duration"));

    json_t *streams = json_object_get(root, "streams");
    size_t index;
    json_t *stream;
    int video_seen = 0;

    json_array_foreach(streams, index, stream) {
        const char *type = json_string_value(json_object_get(stream, "codec_type"));
        json_t *disposition = json_object_get(stream, "disposition");
        int attached = (int)number_of(json_object_get(disposition, "attached_pic"));
        int is_video = type && strcmp(type, "video") == 0;

        if (is_video && !attached && !facts->has_video) {
            facts->has_video = 1;
            facts->video_ordinal = video_seen;
            facts->width = (int)number_of(json_object_get(stream, "width"));
            facts->height = (int)number_of(json_object_get(stream, "height"));
            facts->rotation = rotation_of(stream);
            facts->frame_rate = rate_of(json_object_get(stream, "avg_frame_rate"));

            if (facts->frame_rate <= 0) {
                facts->frame_rate = rate_of(json_object_get(stream, "r_frame_rate"));
            }

            if (facts->duration <= 0) {
                facts->duration = number_of(json_object_get(stream, "duration"));
            }
        }
        else if (type && strcmp(type, "audio") == 0 && !facts->has_audio) {
            facts->has_audio = 1;
            facts->channels = (int)number_of(json_object_get(stream, "channels"));
        }

        if (is_video) {
            video_seen++;
        }
    }

    json_decref(root);

    if (!facts->has_video || facts->width < 2 || facts->height < 2) {
        snprintf(error, error_capacity, "The source has no video stream to package.");

        return -1;
    }

    if (facts->duration <= 0) {
        snprintf(error, error_capacity, "ffprobe could not tell how long the source is.");

        return -1;
    }

    int quarter = ((facts->rotation % 360) + 360) % 360;

    if (quarter == 90 || quarter == 270) {
        int width = facts->width;

        facts->width = facts->height;
        facts->height = width;
    }

    return 0;
}
