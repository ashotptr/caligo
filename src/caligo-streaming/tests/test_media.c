#include "check.h"
#include "media.h"

#include <stdint.h>

static const char reference_key[] = "caligo-test-signing-key-0123456789abcdef";
static const char reference_token[] = "PyqcHnsdSg6behwtPk9aawAAAABo7jtAjOEbrISsGZBuanx2TvJ_zo0XjfZo9DD1";
static const char reference_storage[] = "3/20261014120000-ab12cd34";
static const unsigned char reference_session[MEDIA_SESSION_BYTES] = {
    0x3f, 0x2a, 0x9c, 0x1e, 0x7b, 0x1d, 0x4a, 0x0e, 0x9b, 0x7a, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x6b
};

static const unsigned char *key(void) {
    return (const unsigned char *)reference_key;
}

static size_t key_length(void) {
    return sizeof(reference_key) - 1;
}

static void the_api_token_is_read_back(void) {
    MediaGrant grant;
    char uuid[MEDIA_UUID_TEXT + 1];

    CHECK_EQUAL(0, media_token_read(key(), key_length(), reference_token, strlen(reference_token), &grant));
    CHECK(memcmp(grant.session, reference_session, MEDIA_SESSION_BYTES) == 0);
    CHECK_EQUAL(1760443200LL, grant.expires);
    CHECK(grant.scope == 0x8ce11bac84ac1990ULL);
    CHECK(grant.scope == media_scope(reference_storage));

    media_uuid_text(grant.session, uuid);
    CHECK_TEXT("3f2a9c1e-7b1d-4a0e-9b7a-1c2d3e4f5a6b", uuid);
}

static void writing_gives_the_same_token_as_the_api(void) {
    char token[MEDIA_TOKEN_TEXT + 1];

    CHECK_EQUAL(0, media_token_write(key(), key_length(), reference_session, 1760443200LL, reference_storage, token));
    CHECK_TEXT(reference_token, token);
}

static void any_changed_character_breaks_the_token(void) {
    MediaGrant grant;
    char token[MEDIA_TOKEN_TEXT + 1];
    int refused = 0;

    for (size_t i = 0; i < MEDIA_TOKEN_TEXT; i++) {
        memcpy(token, reference_token, sizeof(token));

        token[i] = token[i] == 'A' ? 'B' : 'A';

        refused += media_token_read(key(), key_length(), token, MEDIA_TOKEN_TEXT, &grant) != 0;
    }

    CHECK_EQUAL(MEDIA_TOKEN_TEXT, refused);
}

static void other_keys_and_shapes_are_refused(void) {
    MediaGrant grant;
    static const char other_key[] = "another-key-another-key-another-key!!";
    char padded[MEDIA_TOKEN_TEXT + 1];

    memcpy(padded, reference_token, sizeof(padded));
    padded[MEDIA_TOKEN_TEXT - 1] = '=';

    CHECK(media_token_read((const unsigned char *)other_key, sizeof(other_key) - 1, reference_token, MEDIA_TOKEN_TEXT, &grant) != 0);
    CHECK(media_token_read(key(), key_length(), reference_token, MEDIA_TOKEN_TEXT - 1, &grant) != 0);
    CHECK(media_token_read(key(), key_length(), padded, MEDIA_TOKEN_TEXT, &grant) != 0);
    CHECK(media_token_read(key(), key_length(), NULL, 0, &grant) != 0);
    CHECK(media_token_read(key(), 0, reference_token, MEDIA_TOKEN_TEXT, &grant) != 0);
}

static void the_internal_key_matches_the_api(void) {
    char hex[MEDIA_INTERNAL_KEY_HEX + 1];

    media_internal_key(key(), key_length(), hex);

    CHECK_TEXT("f6e9386e449212e98b8f06c6666b2f19a23c6992a96d80ef0246aede2d58547e", hex);
}

static void only_plain_relative_paths_pass(void) {
    CHECK(media_path_safe("master.m3u8"));
    CHECK(media_path_safe("subs/2.vtt"));
    CHECK(media_path_safe("seg-0-00001.m4s"));
    CHECK(media_path_safe("3/20261014120000-ab12cd34"));

    CHECK(!media_path_safe(""));
    CHECK(!media_path_safe(NULL));
    CHECK(!media_path_safe("/master.m3u8"));
    CHECK(!media_path_safe("subs//2.vtt"));
    CHECK(!media_path_safe("subs/"));
    CHECK(!media_path_safe("../secret.mp4"));
    CHECK(!media_path_safe("subs/../../secret.mp4"));
    CHECK(!media_path_safe("subs/..%2fsecret.mp4"));
    CHECK(!media_path_safe(".hidden/x.mp4"));
    CHECK(!media_path_safe("subs\\2.vtt"));
    CHECK(!media_path_safe("seg 1.m4s"));
    CHECK(!media_path_safe("seg\n1.m4s"));

    char long_path[MEDIA_PATH_MAX + 2];

    memset(long_path, 'a', sizeof(long_path) - 1);
    long_path[sizeof(long_path) - 1] = '\0';

    CHECK(!media_path_safe(long_path));

    long_path[MEDIA_PATH_MAX] = '\0';

    CHECK(media_path_safe(long_path));
}

static void only_media_files_have_a_type(void) {
    CHECK_TEXT("application/vnd.apple.mpegurl", media_content_type("master.m3u8"));
    CHECK_TEXT("application/dash+xml", media_content_type("manifest.mpd"));
    CHECK_TEXT("video/iso.segment", media_content_type("seg-0-00001.m4s"));
    CHECK_TEXT("video/mp4", media_content_type("progressive.MP4"));
    CHECK_TEXT("text/vtt; charset=utf-8", media_content_type("subs/1.vtt"));
    CHECK(media_content_type("notes.txt") == NULL);
    CHECK(media_content_type("m3u8") == NULL);
    CHECK(media_content_type("job.json") == NULL);
}

static void file_names_are_plain(void) {
    CHECK(media_file_name_plain("Fight Club (1999).mkv"));
    CHECK(media_file_name_plain("clip.English.vtt"));
    CHECK(!media_file_name_plain("../clip.mp4"));
    CHECK(!media_file_name_plain("folder/clip.mp4"));
    CHECK(!media_file_name_plain("folder\\clip.mp4"));
    CHECK(!media_file_name_plain(".hidden.mp4"));
    CHECK(!media_file_name_plain(" clip.mp4"));
    CHECK(!media_file_name_plain("clip.mp4 "));
    CHECK(!media_file_name_plain("C:clip.mp4"));
    CHECK(!media_file_name_plain("clip\t.mp4"));
    CHECK(!media_file_name_plain(""));
}

int main(void) {
    the_api_token_is_read_back();
    writing_gives_the_same_token_as_the_api();
    any_changed_character_breaks_the_token();
    other_keys_and_shapes_are_refused();
    the_internal_key_matches_the_api();
    only_plain_relative_paths_pass();
    only_media_files_have_a_type();
    file_names_are_plain();

    CHECK_REPORT("media");
}
