#include "check.h"
#include "probe.h"

static const char clip[] =
    "{\"streams\": ["
    "{\"index\": 0, \"codec_name\": \"mjpeg\", \"codec_type\": \"video\", \"width\": 300, \"height\": 300,"
    " \"disposition\": {\"attached_pic\": 1}},"
    "{\"index\": 1, \"codec_name\": \"h264\", \"codec_type\": \"video\", \"width\": 1280, \"height\": 720,"
    " \"avg_frame_rate\": \"30000/1001\", \"r_frame_rate\": \"30000/1001\", \"disposition\": {\"attached_pic\": 0}},"
    "{\"index\": 2, \"codec_name\": \"aac\", \"codec_type\": \"audio\", \"channels\": 6},"
    "{\"index\": 3, \"codec_name\": \"aac\", \"codec_type\": \"audio\", \"channels\": 2}],"
    " \"format\": {\"format_name\": \"mov,mp4,m4a,3gp,3g2,mj2\", \"duration\": \"22.012000\"}}";

static const char phone[] =
    "{\"streams\": [{\"codec_type\": \"video\", \"width\": 1920, \"height\": 1080, \"avg_frame_rate\": \"0/0\","
    " \"r_frame_rate\": \"60/1\", \"duration\": \"12.5\","
    " \"side_data_list\": [{\"side_data_type\": \"Display Matrix\", \"rotation\": -90}]}],"
    " \"format\": {\"format_name\": \"matroska,webm\"}}";

static void a_film_with_cover_art_uses_its_real_video(void) {
    SourceFacts facts;
    char error[256] = "";

    CHECK_EQUAL(0, probe_parse(clip, sizeof(clip) - 1, &facts, error, sizeof(error)));
    CHECK_TEXT("mov", facts.demuxer);
    CHECK_EQUAL(1, facts.has_video);
    CHECK_EQUAL(1, facts.video_ordinal);
    CHECK_EQUAL(1280, facts.width);
    CHECK_EQUAL(720, facts.height);
    CHECK(facts.frame_rate > 29.96 && facts.frame_rate < 29.98);
    CHECK(facts.duration > 22.0 && facts.duration < 22.1);
    CHECK_EQUAL(1, facts.has_audio);
    CHECK_EQUAL(6, facts.channels);
}

static void a_rotated_phone_video_is_portrait(void) {
    SourceFacts facts;
    char error[256] = "";

    CHECK_EQUAL(0, probe_parse(phone, sizeof(phone) - 1, &facts, error, sizeof(error)));
    CHECK_TEXT("matroska", facts.demuxer);
    CHECK_EQUAL(1080, facts.width);
    CHECK_EQUAL(1920, facts.height);
    CHECK_EQUAL(-90, facts.rotation);
    CHECK(facts.frame_rate > 59.9 && facts.frame_rate < 60.1);
    CHECK(facts.duration > 12.4 && facts.duration < 12.6);
    CHECK_EQUAL(0, facts.has_audio);
}

static void playlists_and_odd_inputs_are_refused(void) {
    SourceFacts facts;
    char error[256] = "";
    static const char playlist[] = "{\"streams\": [], \"format\": {\"format_name\": \"hls\", \"duration\": \"10\"}}";
    static const char concat[] = "{\"streams\": [], \"format\": {\"format_name\": \"concat\"}}";
    static const char audio_only[] =
        "{\"streams\": [{\"codec_type\": \"audio\", \"channels\": 2}], \"format\": {\"format_name\": \"mov,mp4\", \"duration\": \"3\"}}";
    static const char endless[] =
        "{\"streams\": [{\"codec_type\": \"video\", \"width\": 640, \"height\": 360}], \"format\": {\"format_name\": \"mpegts\"}}";

    CHECK_EQUAL(-1, probe_parse(playlist, sizeof(playlist) - 1, &facts, error, sizeof(error)));
    CHECK(strstr(error, "hls") != NULL);
    CHECK_EQUAL(-1, probe_parse(concat, sizeof(concat) - 1, &facts, error, sizeof(error)));
    CHECK_EQUAL(-1, probe_parse(audio_only, sizeof(audio_only) - 1, &facts, error, sizeof(error)));
    CHECK(strstr(error, "no video") != NULL);
    CHECK_EQUAL(-1, probe_parse(endless, sizeof(endless) - 1, &facts, error, sizeof(error)));
    CHECK_EQUAL(-1, probe_parse("not json", 8, &facts, error, sizeof(error)));
}

static void only_known_demuxers_are_used(void) {
    CHECK_TEXT("mov", probe_demuxer("mov,mp4,m4a,3gp,3g2,mj2"));
    CHECK_TEXT("matroska", probe_demuxer("matroska,webm"));
    CHECK_TEXT("avi", probe_demuxer("avi"));
    CHECK_TEXT("mpegts", probe_demuxer("mpegts"));
    CHECK(probe_demuxer("hls") == NULL);
    CHECK(probe_demuxer("image2") == NULL);
    CHECK(probe_demuxer("tty") == NULL);
    CHECK(probe_demuxer("") == NULL);
    CHECK(probe_demuxer(NULL) == NULL);
}

int main(void) {
    a_film_with_cover_art_uses_its_real_video();
    a_rotated_phone_video_is_portrait();
    playlists_and_odd_inputs_are_refused();
    only_known_demuxers_are_used();

    CHECK_REPORT("probe");
}
