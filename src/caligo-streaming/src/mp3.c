#define _GNU_SOURCE

#include "mp3.h"

#include <string.h>

#define ID3_MAX_TAG (16u * 1024u * 1024u)

static const int BITRATES_V1[16] = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
static const int BITRATES_V2[16] = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

static const int SAMPLE_RATES[4][3] = {
    { 11025, 12000, 8000 },
    { 0, 0, 0 },
    { 22050, 24000, 16000 },
    { 44100, 48000, 32000 }
};

int mp3_parse_header(const unsigned char *bytes, Mp3Header *header) {
    if (bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0) {
        return 0;
    }

    int version_bits = (bytes[1] >> 3) & 0x03;
    int layer_bits = (bytes[1] >> 1) & 0x03;
    int bitrate_index = (bytes[2] >> 4) & 0x0F;
    int rate_index = (bytes[2] >> 2) & 0x03;
    int padding = (bytes[2] >> 1) & 0x01;
    int mode = (bytes[3] >> 6) & 0x03;

    if (version_bits == 1 || layer_bits != 1 || bitrate_index == 0 || bitrate_index == 15 || rate_index == 3) {
        return 0;
    }

    if ((bytes[3] & 0x03) == 2) {
        return 0;
    }

    int mpeg1 = version_bits == 3;

    header->version = mpeg1 ? 1 : (version_bits == 2 ? 2 : 25);
    header->bitrate_kbps = mpeg1 ? BITRATES_V1[bitrate_index] : BITRATES_V2[bitrate_index];
    header->sample_rate = SAMPLE_RATES[version_bits][rate_index];
    header->channels = mode == 3 ? 1 : 2;
    header->samples = mpeg1 ? 1152 : 576;
    header->length = (mpeg1 ? 144000 : 72000) * header->bitrate_kbps / header->sample_rate + padding;

    return header->length >= 24 && header->length <= MP3_MAX_FRAME;
}

int mp3_is_info_frame(const unsigned char *frame, size_t length, const Mp3Header *header) {
    size_t side;

    if (header->version == 1) {
        side = header->channels == 1 ? 17 : 32;
    }
    else {
        side = header->channels == 1 ? 9 : 17;
    }

    size_t offset = 4 + side;

    if (length >= offset + 4 &&
        (memcmp(frame + offset, "Xing", 4) == 0 || memcmp(frame + offset, "Info", 4) == 0)) {
        return 1;
    }

    return length >= 40 && memcmp(frame + 36, "VBRI", 4) == 0;
}

void mp3_framer_init(Mp3Framer *framer, Mp3FrameFn emit, void *context) {
    memset(framer, 0, sizeof(*framer));

    framer->emit = emit;
    framer->context = context;
}

void mp3_framer_reset(Mp3Framer *framer) {
    framer->length = 0;
    framer->skip = 0;
    framer->synced = 0;
}

static int same_stream(const Mp3Header *a, const Mp3Header *b) {
    return a->version == b->version && a->sample_rate == b->sample_rate;
}

static uint64_t id3v2_length(const unsigned char *bytes) {
    if (memcmp(bytes, "ID3", 3) != 0 || bytes[3] < 2 || bytes[3] > 4 || bytes[4] != 0 || (bytes[5] & 0x0F) != 0) {
        return 0;
    }

    if ((bytes[6] | bytes[7] | bytes[8] | bytes[9]) & 0x80) {
        return 0;
    }

    uint64_t size = ((uint64_t)bytes[6] << 21) | ((uint64_t)bytes[7] << 14) | ((uint64_t)bytes[8] << 7) | bytes[9];

    if (size > ID3_MAX_TAG) {
        return 0;
    }

    return size + 10 + ((bytes[5] & 0x10) ? 10 : 0);
}

static void accept_frame(Mp3Framer *framer, const unsigned char *frame, const Mp3Header *header) {
    if (mp3_is_info_frame(frame, (size_t)header->length, header)) {
        framer->info_frames++;

        return;
    }

    if (framer->frames > 0 && (!same_stream(&framer->format, header) || framer->format.channels != header->channels)) {
        framer->format_changes++;
    }

    framer->format = *header;
    framer->frames++;

    framer->emit(framer->context, frame, (size_t)header->length, header);
}

static size_t consume(Mp3Framer *framer) {
    const unsigned char *bytes = framer->buffer;
    size_t length = framer->length;
    size_t position = 0;

    while (position < length) {
        size_t left = length - position;

        if (left < 10) {
            break;
        }

        uint64_t tag = id3v2_length(bytes + position);

        if (tag > 0) {
            size_t here = tag < left ? (size_t)tag : left;

            framer->tag_bytes += tag;
            framer->skip = tag - here;
            position += here;

            continue;
        }

        if (framer->synced && memcmp(bytes + position, "TAG", 3) == 0) {
            if (left < 128) {
                break;
            }

            framer->tag_bytes += 128;
            position += 128;

            continue;
        }

        Mp3Header header;

        if (mp3_parse_header(bytes + position, &header)) {
            size_t frame_length = (size_t)header.length;

            if (framer->synced) {
                if (left < frame_length) {
                    break;
                }

                accept_frame(framer, bytes + position, &header);
                position += frame_length;

                continue;
            }

            if (left < frame_length + 4) {
                break;
            }

            Mp3Header next;

            if (mp3_parse_header(bytes + position + frame_length, &next) && same_stream(&header, &next)) {
                framer->synced = 1;
                framer->resyncs++;

                accept_frame(framer, bytes + position, &header);
                position += frame_length;

                continue;
            }
        }

        framer->synced = 0;
        framer->dropped_bytes++;
        position++;

        const unsigned char *sync = memchr(bytes + position, 0xFF, length - position);
        size_t jump = sync ? (size_t)(sync - (bytes + position)) : length - position;

        const unsigned char *tag_start = jump >= 3 ? memmem(bytes + position, jump, "ID3", 3) : NULL;

        if (tag_start) {
            jump = (size_t)(tag_start - (bytes + position));
        }

        framer->dropped_bytes += jump;
        position += jump;
    }

    return position;
}

void mp3_framer_feed(Mp3Framer *framer, const unsigned char *data, size_t length) {
    while (length > 0) {
        if (framer->skip > 0) {
            size_t here = framer->skip < length ? (size_t)framer->skip : length;

            framer->skip -= here;
            data += here;
            length -= here;

            continue;
        }

        size_t room = MP3_FRAMER_BUFFER - framer->length;
        size_t take = length < room ? length : room;

        memcpy(framer->buffer + framer->length, data, take);

        framer->length += take;
        data += take;
        length -= take;

        size_t used = consume(framer);

        if (used == 0 && framer->length == MP3_FRAMER_BUFFER) {
            used = 1;
            framer->dropped_bytes++;
            framer->synced = 0;
        }

        memmove(framer->buffer, framer->buffer + used, framer->length - used);

        framer->length -= used;
    }
}
