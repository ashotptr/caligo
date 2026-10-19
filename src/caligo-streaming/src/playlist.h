#ifndef CALIGO_PLAYLIST_H
#define CALIGO_PLAYLIST_H

#include <stddef.h>
#include <stdint.h>

#define PLAYLIST_URI_MAX 128
#define MPD_REPRESENTATIONS_MAX 16

typedef struct {
    char uri[PLAYLIST_URI_MAX];
    double duration;
    uint64_t bytes;
} Segment;

typedef struct {
    char init[PLAYLIST_URI_MAX];
    uint64_t init_bytes;
    Segment *segments;
    int count;
    int capacity;
} MediaPlaylist;

typedef struct {
    char id[16];
    char kind[8];
    char codecs[64];
    long bandwidth;
    int width;
    int height;
    double frame_rate;
    int sample_rate;
    int channels;
} Representation;

typedef struct {
    int language_id;
    char label[64];
    char tag[16];
    char vtt[64];
    char playlist[64];
} SubtitleTrack;

typedef struct {
    char path[64];
    double peak;
    double average;
    char codecs[64];
    int width;
    int height;
    double frame_rate;
} VideoVariant;

typedef struct {
    char path[64];
    double peak;
    double average;
    char codecs[64];
    int channels;
} AudioTrack;

void playlist_free(MediaPlaylist *playlist);

int playlist_parse(const char *text, MediaPlaylist *playlist, char *error, size_t error_capacity);

char *playlist_render(const MediaPlaylist *playlist);

void playlist_rates(const MediaPlaylist *playlist, double *peak, double *average, double *duration);

int mpd_representations(const char *mpd, Representation *representations, int capacity);

char *mpd_with_subtitles(const char *mpd, const SubtitleTrack *subtitles, int count);

char *master_render(const VideoVariant *videos, int video_count, const AudioTrack *audio, const SubtitleTrack *subtitles, int subtitle_count);

char *subtitle_playlist_render(const char *vtt_name, double duration);

#endif
