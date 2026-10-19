#include "check.h"
#include "playlist.h"

#include <stdlib.h>

static const char ffmpeg_audio[] =
    "#EXTM3U\n"
    "#EXT-X-VERSION:6\n"
    "#EXT-X-TARGETDURATION:4\n"
    "#EXT-X-MEDIA-SEQUENCE:1\n"
    "#EXT-X-MAP:URI=\"init-3.m4s\"\n"
    "#EXTINF:3.925333,\n"
    "#EXT-X-PROGRAM-DATE-TIME:2026-10-13T22:30:14.000+0000\n"
    "seg-3-00001.m4s\n"
    "#EXTINF:4.010667,\n"
    "#EXT-X-PROGRAM-DATE-TIME:2026-10-13T22:30:17.925+0000\n"
    "seg-3-00002.m4s\n"
    "#EXTINF:2.087188,\r\n"
    "seg-3-00003.m4s\r\n"
    "#EXT-X-ENDLIST\n";

static const char manifest[] =
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
    "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT22.0S\">\n"
    "\t<Period id=\"0\" start=\"PT0.0S\">\n"
    "\t\t<AdaptationSet id=\"0\" contentType=\"video\" startWithSAP=\"1\" frameRate=\"30000/1001\" maxWidth=\"1280\" maxHeight=\"720\">\n"
    "\t\t\t<Representation id=\"0\" mimeType=\"video/mp4\" codecs=\"avc1.64001f\" bandwidth=\"2800000\" width=\"1280\" height=\"720\" sar=\"1:1\">\n"
    "\t\t\t\t<SegmentTemplate timescale=\"12800\" initialization=\"init-$RepresentationID$.m4s\" media=\"seg-$RepresentationID$-$Number%05d$.m4s\" startNumber=\"1\">\n"
    "\t\t\t\t</SegmentTemplate>\n"
    "\t\t\t</Representation>\n"
    "\t\t\t<Representation id=\"1\" mimeType=\"video/mp4\" codecs=\"avc1.64001e\" bandwidth=\"1400000\" width=\"854\" height=\"480\" sar=\"1280:1281\">\n"
    "\t\t\t</Representation>\n"
    "\t\t</AdaptationSet>\n"
    "\t\t<AdaptationSet id=\"1\" contentType=\"audio\" startWithSAP=\"1\" lang=\"und\">\n"
    "\t\t\t<Representation id=\"3\" mimeType=\"audio/mp4\" codecs=\"mp4a.40.2\" bandwidth=\"128000\" audioSamplingRate=\"48000\">\n"
    "\t\t\t\t<AudioChannelConfiguration schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\" />\n"
    "\t\t\t</Representation>\n"
    "\t\t</AdaptationSet>\n"
    "\t</Period>\n"
    "</MPD>\n";

static void an_ffmpeg_playlist_is_read(void) {
    MediaPlaylist playlist;
    char error[256] = "";

    CHECK_EQUAL(0, playlist_parse(ffmpeg_audio, &playlist, error, sizeof(error)));
    CHECK_TEXT("init-3.m4s", playlist.init);
    CHECK_EQUAL(3, playlist.count);
    CHECK_TEXT("seg-3-00003.m4s", playlist.segments[2].uri);
    CHECK(playlist.segments[1].duration > 4.0106 && playlist.segments[1].duration < 4.0107);

    playlist.init_bytes = 800;
    playlist.segments[0].bytes = 64000;
    playlist.segments[1].bytes = 66000;
    playlist.segments[2].bytes = 30000;

    double peak = 0;
    double average = 0;
    double duration = 0;

    playlist_rates(&playlist, &peak, &average, &duration);

    CHECK(duration > 10.02 && duration < 10.03);
    CHECK((long long)peak == (long long)((66000.0 + 800.0) * 8.0 / 4.010667));
    CHECK((long long)average == (long long)((800.0 + 64000.0 + 66000.0 + 30000.0) * 8.0 / duration));

    char *rendered = playlist_render(&playlist);

    CHECK(rendered != NULL);
    CHECK_TEXT(
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n"
        "#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MAP:URI=\"init-3.m4s\"\n"
        "#EXTINF:3.925333,\nseg-3-00001.m4s\n#EXTINF:4.010667,\nseg-3-00002.m4s\n#EXTINF:2.087188,\nseg-3-00003.m4s\n"
        "#EXT-X-ENDLIST\n",
        rendered);

    free(rendered);
    playlist_free(&playlist);
}

static void a_long_segment_raises_the_target_duration(void) {
    MediaPlaylist playlist;
    char error[256] = "";
    static const char text[] = "#EXTM3U\n#EXT-X-MAP:URI=\"init-0.m4s\"\n#EXTINF:4.5,\nseg-0-00001.m4s\n#EXTINF:6.49,\nseg-0-00002.m4s\n";

    CHECK_EQUAL(0, playlist_parse(text, &playlist, error, sizeof(error)));

    char *rendered = playlist_render(&playlist);

    CHECK(strstr(rendered, "#EXT-X-TARGETDURATION:6\n") != NULL);

    free(rendered);
    playlist_free(&playlist);
}

static void unexpected_playlists_are_refused(void) {
    MediaPlaylist playlist;
    char error[256] = "";

    CHECK_EQUAL(-1, playlist_parse("#EXTM3U\n#EXTINF:4,\nseg.m4s\n", &playlist, error, sizeof(error)));
    playlist_free(&playlist);
    CHECK_EQUAL(-1, playlist_parse("#EXTM3U\n#EXT-X-MAP:URI=\"init.m4s\"\n", &playlist, error, sizeof(error)));
    playlist_free(&playlist);
    CHECK_EQUAL(-1, playlist_parse("#EXTM3U\n#EXT-X-MAP:URI=\"init.m4s\"\n#EXTINF:4,\n../../etc/passwd\n", &playlist, error, sizeof(error)));
    playlist_free(&playlist);
    CHECK_EQUAL(-1, playlist_parse("#EXTM3U\n#EXT-X-MAP:URI=\"init.m4s\"\n#EXTINF:4,\nhttp://example.com/x.m4s\n", &playlist, error, sizeof(error)));
    playlist_free(&playlist);
    CHECK_EQUAL(-1, playlist_parse("#EXTM3U\n#EXT-X-MAP:URI=\"init.m4s\"\n#EXTINF:,\nseg.m4s\n", &playlist, error, sizeof(error)));
    playlist_free(&playlist);
}

static void the_manifest_tells_what_ffmpeg_made(void) {
    Representation representations[MPD_REPRESENTATIONS_MAX];
    int count = mpd_representations(manifest, representations, MPD_REPRESENTATIONS_MAX);

    CHECK_EQUAL(3, count);
    CHECK_TEXT("0", representations[0].id);
    CHECK_TEXT("video", representations[0].kind);
    CHECK_TEXT("avc1.64001f", representations[0].codecs);
    CHECK_EQUAL(2800000, representations[0].bandwidth);
    CHECK_EQUAL(1280, representations[0].width);
    CHECK_EQUAL(720, representations[0].height);
    CHECK(representations[0].frame_rate > 29.96 && representations[0].frame_rate < 29.98);
    CHECK_EQUAL(854, representations[1].width);
    CHECK_TEXT("3", representations[2].id);
    CHECK_TEXT("audio", representations[2].kind);
    CHECK_TEXT("mp4a.40.2", representations[2].codecs);
    CHECK_EQUAL(48000, representations[2].sample_rate);
    CHECK_EQUAL(2, representations[2].channels);
    CHECK_EQUAL(0, representations[2].width);

    CHECK_EQUAL(-1, mpd_representations(manifest, representations, 2));
}

static void subtitles_are_added_to_the_manifest(void) {
    SubtitleTrack subtitles[2] = {
        { 2, "English", "en", "subs/2.vtt", "subs/2.m3u8" },
        { 9, "Tom & \"Jerry\" <x>", "", "subs/9.vtt", "subs/9.m3u8" }
    };

    char *patched = mpd_with_subtitles(manifest, subtitles, 2);

    CHECK(patched != NULL);
    CHECK(strstr(patched, "<AdaptationSet id=\"100\" contentType=\"text\" mimeType=\"text/vtt\" lang=\"en\">") != NULL);
    CHECK(strstr(patched, "<BaseURL>subs/2.vtt</BaseURL>") != NULL);
    CHECK(strstr(patched, "<Label>Tom &amp; &quot;Jerry&quot; &lt;x&gt;</Label>") != NULL);
    CHECK(strstr(patched, "lang=\"und\"") != NULL);
    CHECK(strstr(patched, "</AdaptationSet>\n\t</Period>\n</MPD>\n") != NULL);
    CHECK(strstr(patched, "<Representation id=\"0\" mimeType=\"video/mp4\"") != NULL);

    Representation representations[MPD_REPRESENTATIONS_MAX];

    CHECK_EQUAL(5, mpd_representations(patched, representations, MPD_REPRESENTATIONS_MAX));
    CHECK_TEXT("text", representations[3].kind);

    free(patched);

    char *unchanged = mpd_with_subtitles(manifest, NULL, 0);

    CHECK_TEXT(manifest, unchanged);
    free(unchanged);

    CHECK(mpd_with_subtitles("<MPD></MPD>", subtitles, 1) == NULL);
}

static void the_master_playlist_starts_near_two_megabits(void) {
    VideoVariant videos[3] = {
        { "video-0.m3u8", 3100000, 2900000, "avc1.64001f", 1280, 720, 29.97003 },
        { "video-2.m3u8", 900000, 800000, "avc1.64001e", 640, 360, 29.97003 },
        { "video-1.m3u8", 1600000, 1450000, "avc1.64001e", 854, 480, 29.97003 }
    };
    AudioTrack audio = { "audio-3.m3u8", 135000.4, 130000.2, "mp4a.40.2", 2 };
    SubtitleTrack subtitle = { 2, "English", "en", "subs/2.vtt", "subs/2.m3u8" };

    char *master = master_render(videos, 3, &audio, &subtitle, 1);

    CHECK(master != NULL);
    CHECK_TEXT(
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n"
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Audio\",DEFAULT=YES,AUTOSELECT=YES,CHANNELS=\"2\",URI=\"audio-3.m3u8\"\n"
        "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"subs/2.m3u8\"\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=1735001,AVERAGE-BANDWIDTH=1580001,CODECS=\"avc1.64001e,mp4a.40.2\",RESOLUTION=854x480,FRAME-RATE=29.970,AUDIO=\"audio\",SUBTITLES=\"subs\"\n"
        "video-1.m3u8\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=3235001,AVERAGE-BANDWIDTH=3030001,CODECS=\"avc1.64001f,mp4a.40.2\",RESOLUTION=1280x720,FRAME-RATE=29.970,AUDIO=\"audio\",SUBTITLES=\"subs\"\n"
        "video-0.m3u8\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=1035001,AVERAGE-BANDWIDTH=930001,CODECS=\"avc1.64001e,mp4a.40.2\",RESOLUTION=640x360,FRAME-RATE=29.970,AUDIO=\"audio\",SUBTITLES=\"subs\"\n"
        "video-2.m3u8\n",
        master);

    free(master);
}

static void a_silent_film_has_no_audio_group(void) {
    VideoVariant video = { "video-0.m3u8", 3500000, 3000000, "avc1.64001f", 1280, 720, 0 };
    char *master = master_render(&video, 1, NULL, NULL, 0);

    CHECK(master != NULL);
    CHECK(strstr(master, "AUDIO") == NULL);
    CHECK(strstr(master, "SUBTITLES") == NULL);
    CHECK(strstr(master, "FRAME-RATE") == NULL);
    CHECK(strstr(master, "CODECS=\"avc1.64001f\",RESOLUTION=1280x720\nvideo-0.m3u8\n") != NULL);

    free(master);

    CHECK(master_render(&video, 0, NULL, NULL, 0) == NULL);
}

static void a_subtitle_playlist_covers_the_whole_title(void) {
    char *playlist = subtitle_playlist_render("2.vtt", 5400.25);

    CHECK_TEXT(
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:5401\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n"
        "#EXTINF:5400.250,\n2.vtt\n#EXT-X-ENDLIST\n",
        playlist);

    free(playlist);
}

int main(void) {
    an_ffmpeg_playlist_is_read();
    a_long_segment_raises_the_target_duration();
    unexpected_playlists_are_refused();
    the_manifest_tells_what_ffmpeg_made();
    subtitles_are_added_to_the_manifest();
    the_master_playlist_starts_near_two_megabits();
    a_silent_film_has_no_audio_group();
    a_subtitle_playlist_covers_the_whole_title();

    CHECK_REPORT("playlist");
}
