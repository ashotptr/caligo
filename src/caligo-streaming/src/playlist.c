#define _GNU_SOURCE

#include "playlist.h"
#include "media.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

typedef struct {
    char *data;
    size_t length;
    size_t capacity;
    int failed;
} Text;

static void text_add(Text *text, const char *format, ...) {
    if (text->failed) {
        return;
    }

    va_list arguments;

    va_start(arguments, format);

    int needed = vsnprintf(NULL, 0, format, arguments);

    va_end(arguments);

    if (needed < 0) {
        text->failed = 1;

        return;
    }

    size_t want = text->length + (size_t)needed + 1;

    if (want > text->capacity) {
        size_t capacity = text->capacity ? text->capacity : 1024;

        while (capacity < want) {
            capacity *= 2;
        }

        char *grown = realloc(text->data, capacity);

        if (!grown) {
            text->failed = 1;

            return;
        }

        text->data = grown;
        text->capacity = capacity;
    }

    va_start(arguments, format);
    vsnprintf(text->data + text->length, text->capacity - text->length, format, arguments);
    va_end(arguments);

    text->length += (size_t)needed;
}

static char *text_finish(Text *text) {
    if (text->failed || !text->data) {
        free(text->data);

        return NULL;
    }

    return text->data;
}

static void xml_add(Text *text, const char *value) {
    for (const char *c = value; *c; c++) {
        switch (*c) {
            case '&': text_add(text, "&amp;"); break;
            case '<': text_add(text, "&lt;"); break;
            case '>': text_add(text, "&gt;"); break;
            case '"': text_add(text, "&quot;"); break;
            case '\'': text_add(text, "&apos;"); break;
            default: text_add(text, "%c", *c); break;
        }
    }
}

static void quoted_add(Text *text, const char *value) {
    for (const char *c = value; *c; c++) {
        if (*c != '"' && *c != '\r' && *c != '\n') {
            text_add(text, "%c", *c);
        }
    }
}

static int push_segment(MediaPlaylist *playlist, const char *uri, size_t length, double duration) {
    if (playlist->count == playlist->capacity) {
        int capacity = playlist->capacity ? playlist->capacity * 2 : 64;
        Segment *grown = realloc(playlist->segments, (size_t)capacity * sizeof(Segment));

        if (!grown) {
            return -1;
        }

        playlist->segments = grown;
        playlist->capacity = capacity;
    }

    Segment *segment = &playlist->segments[playlist->count++];

    if (length >= sizeof(segment->uri)) {
        length = sizeof(segment->uri) - 1;
    }

    memcpy(segment->uri, uri, length);
    segment->uri[length] = '\0';

    segment->duration = duration;
    segment->bytes = 0;

    return 0;
}

static int parse_lines(const char *text, MediaPlaylist *playlist, char *error, size_t error_capacity) {

    double pending = -1;
    const char *line = text;

    while (line && *line) {
        const char *end = strchr(line, '\n');
        size_t length = end ? (size_t)(end - line) : strlen(line);
        char current[512];

        while (length > 0 && (line[length - 1] == '\r' || line[length - 1] == ' ')) {
            length--;
        }

        if (length >= sizeof(current)) {
            snprintf(error, error_capacity, "A playlist line is too long.");

            return -1;
        }

        memcpy(current, line, length);
        current[length] = '\0';

        if (strncmp(current, "#EXT-X-MAP:", 11) == 0) {
            const char *uri = strstr(current, "URI=\"");
            const char *close = uri ? strchr(uri + 5, '"') : NULL;

            if (!close || (size_t)(close - uri - 5) >= sizeof(playlist->init)) {
                snprintf(error, error_capacity, "The playlist's EXT-X-MAP has no URI.");

                return -1;
            }

            memcpy(playlist->init, uri + 5, (size_t)(close - uri - 5));
            playlist->init[close - uri - 5] = '\0';
        }
        else if (strncmp(current, "#EXTINF:", 8) == 0) {
            char *after = NULL;

            pending = strtod(current + 8, &after);

            if (after == current + 8 || pending <= 0) {
                snprintf(error, error_capacity, "The playlist has a segment without a duration.");

                return -1;
            }
        }
        else if (length > 0 && current[0] != '#') {
            if (pending <= 0 || !media_path_safe(current) || length >= PLAYLIST_URI_MAX) {
                snprintf(error, error_capacity, "The playlist names an unexpected segment: %.80s", current);

                return -1;
            }

            if (push_segment(playlist, current, length, pending) != 0) {
                snprintf(error, error_capacity, "Out of memory reading the playlist.");

                return -1;
            }

            pending = -1;
        }

        line = end ? end + 1 : NULL;
    }

    if (playlist->count == 0 || !playlist->init[0] || !media_path_safe(playlist->init)) {
        snprintf(error, error_capacity, "The playlist has no segments or no initialization section.");

        return -1;
    }

    return 0;
}

int playlist_parse(const char *text, MediaPlaylist *playlist, char *error, size_t error_capacity) {
    memset(playlist, 0, sizeof(*playlist));

    if (parse_lines(text, playlist, error, error_capacity) != 0) {
        playlist_free(playlist);

        return -1;
    }

    return 0;
}

void playlist_free(MediaPlaylist *playlist) {
    free(playlist->segments);

    playlist->segments = NULL;
    playlist->count = 0;
    playlist->capacity = 0;
}

char *playlist_render(const MediaPlaylist *playlist) {
    Text text = { 0 };
    int target = 1;

    for (int i = 0; i < playlist->count; i++) {
        int rounded = (int)floor(playlist->segments[i].duration + 0.5);

        if (rounded > target) {
            target = rounded;
        }
    }

    text_add(&text, "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:%d\n#EXT-X-MEDIA-SEQUENCE:0\n", target);
    text_add(&text, "#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MAP:URI=\"%s\"\n", playlist->init);

    for (int i = 0; i < playlist->count; i++) {
        text_add(&text, "#EXTINF:%.6f,\n%s\n", playlist->segments[i].duration, playlist->segments[i].uri);
    }

    text_add(&text, "#EXT-X-ENDLIST\n");

    return text_finish(&text);
}

void playlist_rates(const MediaPlaylist *playlist, double *peak, double *average, double *duration) {
    double total_bytes = (double)playlist->init_bytes;
    double total_duration = 0;
    double highest = 0;

    for (int i = 0; i < playlist->count; i++) {
        const Segment *segment = &playlist->segments[i];
        double rate = ((double)segment->bytes + (double)playlist->init_bytes) * 8.0 / segment->duration;

        if (rate > highest) {
            highest = rate;
        }

        total_bytes += (double)segment->bytes;
        total_duration += segment->duration;
    }

    *peak = highest;
    *average = total_duration > 0 ? total_bytes * 8.0 / total_duration : 0;
    *duration = total_duration;
}

static int attribute(const char *tag, const char *tag_end, const char *name, char *value, size_t capacity) {
    size_t name_length = strlen(name);

    for (const char *cursor = tag; cursor && cursor < tag_end; cursor++) {
        cursor = strstr(cursor, name);

        if (!cursor || cursor >= tag_end) {
            return 0;
        }

        if ((cursor[-1] == ' ' || cursor[-1] == '\t' || cursor[-1] == '\n') &&
            cursor[name_length] == '=' && cursor[name_length + 1] == '"') {
            const char *start = cursor + name_length + 2;
            const char *close = memchr(start, '"', (size_t)(tag_end - start));

            if (!close || (size_t)(close - start) >= capacity) {
                return 0;
            }

            memcpy(value, start, (size_t)(close - start));
            value[close - start] = '\0';

            return 1;
        }
    }

    return 0;
}

static double frame_rate_of(const char *text) {
    char *end = NULL;
    double numerator = strtod(text, &end);

    if (!end || end == text) {
        return 0;
    }

    if (*end == '/') {
        double denominator = strtod(end + 1, NULL);

        return denominator > 0 ? numerator / denominator : 0;
    }

    return numerator;
}

int mpd_representations(const char *mpd, Representation *representations, int capacity) {
    int count = 0;
    char set_type[16] = "";
    char set_rate[32] = "";
    char value[128];
    Representation *current = NULL;

    for (const char *cursor = strchr(mpd, '<'); cursor; cursor = strchr(cursor + 1, '<')) {
        const char *tag_end = strchr(cursor, '>');

        if (!tag_end) {
            break;
        }

        if (strncmp(cursor, "<AdaptationSet", 14) == 0 && (cursor[14] == ' ' || cursor[14] == '>')) {
            set_type[0] = '\0';
            set_rate[0] = '\0';
            current = NULL;

            attribute(cursor, tag_end, "contentType", set_type, sizeof(set_type));
            attribute(cursor, tag_end, "frameRate", set_rate, sizeof(set_rate));
        }
        else if (strncmp(cursor, "<Representation", 15) == 0 && cursor[15] == ' ') {
            if (count == capacity) {
                return -1;
            }

            current = &representations[count++];

            memset(current, 0, sizeof(*current));
            attribute(cursor, tag_end, "id", current->id, sizeof(current->id));
            attribute(cursor, tag_end, "codecs", current->codecs, sizeof(current->codecs));

            if (attribute(cursor, tag_end, "mimeType", value, sizeof(value))) {
                snprintf(current->kind, sizeof(current->kind), "%s",
                         strncmp(value, "video/", 6) == 0 ? "video" : strncmp(value, "audio/", 6) == 0 ? "audio" : "other");
            }
            else {
                snprintf(current->kind, sizeof(current->kind), "%s", set_type[0] ? set_type : "other");
            }

            if (attribute(cursor, tag_end, "bandwidth", value, sizeof(value))) {
                current->bandwidth = strtol(value, NULL, 10);
            }

            if (attribute(cursor, tag_end, "width", value, sizeof(value))) {
                current->width = (int)strtol(value, NULL, 10);
            }

            if (attribute(cursor, tag_end, "height", value, sizeof(value))) {
                current->height = (int)strtol(value, NULL, 10);
            }

            if (attribute(cursor, tag_end, "audioSamplingRate", value, sizeof(value))) {
                current->sample_rate = (int)strtol(value, NULL, 10);
            }

            current->frame_rate = attribute(cursor, tag_end, "frameRate", value, sizeof(value))
                ? frame_rate_of(value)
                : frame_rate_of(set_rate);
        }
        else if (current && strncmp(cursor, "<AudioChannelConfiguration", 26) == 0 &&
                 attribute(cursor, tag_end, "value", value, sizeof(value))) {
            current->channels = (int)strtol(value, NULL, 10);
        }
        else if (strncmp(cursor, "</Representation", 16) == 0 || strncmp(cursor, "</AdaptationSet", 15) == 0) {
            current = NULL;
        }
    }

    return count;
}

char *mpd_with_subtitles(const char *mpd, const SubtitleTrack *subtitles, int count) {
    const char *period_end = NULL;

    for (const char *found = strstr(mpd, "</Period>"); found; found = strstr(found + 1, "</Period>")) {
        period_end = found;
    }

    if (!period_end) {
        return NULL;
    }

    const char *line_start = period_end;

    while (line_start > mpd && (line_start[-1] == '\t' || line_start[-1] == ' ')) {
        line_start--;
    }

    Text text = { 0 };

    text_add(&text, "%.*s", (int)(line_start - mpd), mpd);

    for (int i = 0; i < count; i++) {
        const SubtitleTrack *subtitle = &subtitles[i];

        text_add(&text, "\t\t<AdaptationSet id=\"%d\" contentType=\"text\" mimeType=\"text/vtt\" lang=\"", 100 + i);
        xml_add(&text, subtitle->tag[0] ? subtitle->tag : "und");
        text_add(&text, "\">\n\t\t\t<Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"subtitle\" />\n\t\t\t<Label>");
        xml_add(&text, subtitle->label);
        text_add(&text, "</Label>\n\t\t\t<Representation id=\"subtitle-%d\" bandwidth=\"256\">\n\t\t\t\t<BaseURL>", subtitle->language_id);
        xml_add(&text, subtitle->vtt);
        text_add(&text, "</BaseURL>\n\t\t\t</Representation>\n\t\t</AdaptationSet>\n");
    }

    text_add(&text, "%s", line_start);

    return text_finish(&text);
}

char *master_render(const VideoVariant *videos, int video_count, const AudioTrack *audio, const SubtitleTrack *subtitles, int subtitle_count) {
    Text text = { 0 };
    int order[64];
    int ordered = 0;
    int start = -1;

    if (video_count <= 0 || video_count > 64) {
        return NULL;
    }

    double audio_peak = audio ? audio->peak : 0;
    double audio_average = audio ? audio->average : 0;

    for (int i = 0; i < video_count; i++) {
        double peak = videos[i].peak + audio_peak;

        if (peak <= 2000000.0 && (start < 0 || peak > videos[start].peak + audio_peak)) {
            start = i;
        }
    }

    if (start < 0) {
        for (int i = 0; i < video_count; i++) {
            if (start < 0 || videos[i].peak < videos[start].peak) {
                start = i;
            }
        }
    }

    order[ordered++] = start;

    for (int pass = 0; pass < video_count; pass++) {
        int best = -1;

        for (int i = 0; i < video_count; i++) {
            int used = 0;

            for (int j = 0; j < ordered; j++) {
                used |= order[j] == i;
            }

            if (!used && (best < 0 || videos[i].peak > videos[best].peak)) {
                best = i;
            }
        }

        if (best >= 0) {
            order[ordered++] = best;
        }
    }

    text_add(&text, "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n");

    if (audio) {
        text_add(&text, "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Audio\",DEFAULT=YES,AUTOSELECT=YES");

        if (audio->channels > 0) {
            text_add(&text, ",CHANNELS=\"%d\"", audio->channels);
        }

        text_add(&text, ",URI=\"%s\"\n", audio->path);
    }

    for (int i = 0; i < subtitle_count; i++) {
        text_add(&text, "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"");
        quoted_add(&text, subtitles[i].label);
        text_add(&text, "\"");

        if (subtitles[i].tag[0]) {
            text_add(&text, ",LANGUAGE=\"");
            quoted_add(&text, subtitles[i].tag);
            text_add(&text, "\"");
        }

        text_add(&text, ",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"%s\"\n", subtitles[i].playlist);
    }

    for (int k = 0; k < ordered; k++) {
        const VideoVariant *video = &videos[order[k]];

        text_add(&text, "#EXT-X-STREAM-INF:BANDWIDTH=%.0f,AVERAGE-BANDWIDTH=%.0f,CODECS=\"%s%s%s\",RESOLUTION=%dx%d",
                 ceil(video->peak + audio_peak), ceil(video->average + audio_average),
                 video->codecs, audio ? "," : "", audio ? audio->codecs : "", video->width, video->height);

        if (video->frame_rate > 0) {
            text_add(&text, ",FRAME-RATE=%.3f", video->frame_rate);
        }

        if (audio) {
            text_add(&text, ",AUDIO=\"audio\"");
        }

        if (subtitle_count > 0) {
            text_add(&text, ",SUBTITLES=\"subs\"");
        }

        text_add(&text, "\n%s\n", video->path);
    }

    return text_finish(&text);
}

char *subtitle_playlist_render(const char *vtt_name, double duration) {
    Text text = { 0 };
    int target = (int)ceil(duration);

    text_add(&text, "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:%d\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n",
             target < 1 ? 1 : target);
    text_add(&text, "#EXTINF:%.3f,\n%s\n#EXT-X-ENDLIST\n", duration, vtt_name);

    return text_finish(&text);
}
