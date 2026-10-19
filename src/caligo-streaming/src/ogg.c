#define _GNU_SOURCE

#include "ogg.h"

#include <string.h>
#include <strings.h>

static uint32_t crc_table[256];
static int crc_ready;

static void crc_init(void) {
    for (uint32_t index = 0; index < 256; index++) {
        uint32_t value = index << 24;

        for (int bit = 0; bit < 8; bit++) {
            value = (value & 0x80000000u) ? (value << 1) ^ 0x04C11DB7u : value << 1;
        }

        crc_table[index] = value;
    }

    crc_ready = 1;
}

static uint32_t read32(const unsigned char *bytes) {
    return (uint32_t)bytes[0] | (uint32_t)bytes[1] << 8 | (uint32_t)bytes[2] << 16 | (uint32_t)bytes[3] << 24;
}

static uint64_t read64(const unsigned char *bytes) {
    return (uint64_t)read32(bytes) | (uint64_t)read32(bytes + 4) << 32;
}

static int read16(const unsigned char *bytes) {
    return bytes[0] | bytes[1] << 8;
}

static void write32(unsigned char *bytes, uint32_t value) {
    bytes[0] = (unsigned char)value;
    bytes[1] = (unsigned char)(value >> 8);
    bytes[2] = (unsigned char)(value >> 16);
    bytes[3] = (unsigned char)(value >> 24);
}

static void write64(unsigned char *bytes, uint64_t value) {
    write32(bytes, (uint32_t)value);
    write32(bytes + 4, (uint32_t)(value >> 32));
}

uint32_t ogg_page_checksum(const unsigned char *page, size_t length) {
    uint32_t crc = 0;

    if (!crc_ready) {
        crc_init();
    }

    for (size_t index = 0; index < length; index++) {
        uint32_t byte = index >= 22 && index < 26 ? 0 : page[index];

        crc = (crc << 8) ^ crc_table[((crc >> 24) ^ byte) & 0xFF];
    }

    return crc;
}

int ogg_page_parse(const unsigned char *bytes, size_t length, OggPage *page) {
    if (length < OGG_HEADER_BYTES) {
        return 0;
    }

    if (memcmp(bytes, "OggS", 4) != 0 || bytes[4] != 0) {
        return -1;
    }

    size_t segments = bytes[26];

    if (length < OGG_HEADER_BYTES + segments) {
        return 0;
    }

    size_t body = 0;

    for (size_t index = 0; index < segments; index++) {
        body += bytes[OGG_HEADER_BYTES + index];
    }

    size_t total = OGG_HEADER_BYTES + segments + body;

    if (length < total) {
        return 0;
    }

    if (read32(bytes + 22) != ogg_page_checksum(bytes, total)) {
        return -2;
    }

    page->bytes = bytes;
    page->length = total;
    page->flags = bytes[5];
    page->granule = read64(bytes + 6);
    page->serial = read32(bytes + 14);
    page->sequence = read32(bytes + 18);
    page->segments = (int)segments;
    page->lacing = bytes + OGG_HEADER_BYTES;
    page->body = bytes + OGG_HEADER_BYTES + segments;
    page->body_length = body;

    return (int)total;
}

size_t ogg_page_build(
    unsigned char *out, size_t capacity, int flags, uint64_t granule, uint32_t serial, uint32_t sequence,
    const unsigned char *packet, size_t packet_length) {
    size_t segments = packet_length / 255 + 1;

    if (segments > 255) {
        return 0;
    }

    size_t total = OGG_HEADER_BYTES + segments + packet_length;

    if (total > capacity) {
        return 0;
    }

    memcpy(out, "OggS", 4);

    out[4] = 0;
    out[5] = (unsigned char)flags;

    write64(out + 6, granule);
    write32(out + 14, serial);
    write32(out + 18, sequence);
    write32(out + 22, 0);

    out[26] = (unsigned char)segments;

    for (size_t index = 0; index + 1 < segments; index++) {
        out[OGG_HEADER_BYTES + index] = 255;
    }

    out[OGG_HEADER_BYTES + segments - 1] = (unsigned char)(packet_length % 255);

    memcpy(out + OGG_HEADER_BYTES + segments, packet, packet_length);
    write32(out + 22, ogg_page_checksum(out, total));

    return total;
}

void ogg_page_rewrite(unsigned char *page, size_t length, int flags, uint64_t granule, uint32_t serial, uint32_t sequence) {
    page[5] = (unsigned char)flags;

    write64(page + 6, granule);
    write32(page + 14, serial);
    write32(page + 18, sequence);
    write32(page + 22, ogg_page_checksum(page, length));
}

void ogg_reader_init(OggReader *reader, OggPageFn emit, void *context) {
    memset(reader, 0, sizeof(*reader));

    reader->emit = emit;
    reader->context = context;
}

void ogg_reader_reset(OggReader *reader) {
    reader->length = 0;
}

static size_t reader_scan(OggReader *reader) {
    size_t position = 0;

    while (reader->length - position >= 4) {
        const unsigned char *found = memmem(reader->buffer + position, reader->length - position, "OggS", 4);

        if (!found) {
            size_t drop = reader->length - position - 3;

            reader->dropped_bytes += drop;
            position += drop;

            break;
        }

        size_t skip = (size_t)(found - (reader->buffer + position));

        reader->dropped_bytes += skip;
        position += skip;

        OggPage page;
        int result = ogg_page_parse(reader->buffer + position, reader->length - position, &page);

        if (result == 0) {
            break;
        }

        if (result < 0) {
            if (result == -2) {
                reader->crc_errors++;
            }

            reader->dropped_bytes++;
            position++;

            continue;
        }

        reader->pages++;
        reader->emit(reader->context, &page);

        position += (size_t)result;
    }

    return position;
}

void ogg_reader_feed(OggReader *reader, const unsigned char *data, size_t length) {
    while (length > 0) {
        size_t room = sizeof(reader->buffer) - reader->length;
        size_t take = length < room ? length : room;

        memcpy(reader->buffer + reader->length, data, take);

        reader->length += take;
        data += take;
        length -= take;

        size_t used = reader_scan(reader);

        if (used == 0 && take == 0) {
            reader->dropped_bytes += reader->length;
            reader->length = 0;

            continue;
        }

        if (used > 0) {
            memmove(reader->buffer, reader->buffer + used, reader->length - used);

            reader->length -= used;
        }
    }
}

static int frame_samples(unsigned char toc) {
    static const int silk[4] = { 480, 960, 1920, 2880 };
    static const int celt[4] = { 120, 240, 480, 960 };
    int config = toc >> 3;

    if (config < 12) {
        return silk[config & 3];
    }

    if (config < 16) {
        return (config & 1) ? 960 : 480;
    }

    return celt[config & 3];
}

int opus_packet_samples(const unsigned char *start, size_t available) {
    if (available < 1) {
        return -1;
    }

    int code = start[0] & 3;
    int frames;

    if (code == 0) {
        frames = 1;
    }
    else if (code < 3) {
        frames = 2;
    }
    else {
        if (available < 2) {
            return -1;
        }

        frames = start[1] & 0x3F;

        if (frames == 0) {
            return -1;
        }
    }

    int samples = frame_samples(start[0]) * frames;

    return samples > 5760 ? -1 : samples;
}

int opus_head_parse(const unsigned char *packet, size_t length, OpusHead *head) {
    memset(head, 0, sizeof(*head));

    if (length < 19 || memcmp(packet, "OpusHead", 8) != 0 || (packet[8] & 0xF0) != 0 || packet[9] == 0) {
        return 0;
    }

    head->version = packet[8];
    head->channels = packet[9];
    head->pre_skip = read16(packet + 10);
    head->input_rate = read32(packet + 12);
    head->gain = read16(packet + 16) >= 32768 ? read16(packet + 16) - 65536 : read16(packet + 16);
    head->family = packet[18];

    if (head->family == 0) {
        if (head->channels > 2) {
            return 0;
        }

        head->streams = 1;
        head->coupled = head->channels - 1;

        return 1;
    }

    if (length < 21 + (size_t)head->channels) {
        return 0;
    }

    head->streams = packet[19];
    head->coupled = packet[20];

    if (head->streams == 0 || head->coupled > head->streams) {
        return 0;
    }

    memcpy(head->mapping, packet + 21, (size_t)head->channels);

    return 1;
}

int opus_head_compatible(const OpusHead *first, const OpusHead *second) {
    return first->channels == second->channels && first->family == second->family &&
        first->streams == second->streams && first->coupled == second->coupled &&
        (first->family == 0 || memcmp(first->mapping, second->mapping, (size_t)first->channels) == 0);
}

static void copy_comment(char *target, const char *source, size_t length) {
    if (length >= OPUS_TEXT) {
        length = OPUS_TEXT - 1;

        while (length > 0 && ((unsigned char)source[length] & 0xC0) == 0x80) {
            length--;
        }
    }

    for (size_t index = 0; index < length; index++) {
        unsigned char byte = (unsigned char)source[index];

        if (byte < 0x20 || byte == 0x7F) {
            target[index] = ' ';
        }
        else {
            target[index] = (char)byte;
        }
    }

    target[length] = '\0';
}

static void take_comment(OpusTitle *title, const char *comment, size_t length) {
    static const struct { const char *name; size_t offset; } fields[] = {
        { "TITLE", offsetof(OpusTitle, title) },
        { "ARTIST", offsetof(OpusTitle, artist) },
        { "ALBUM", offsetof(OpusTitle, album) },
    };

    for (size_t index = 0; index < sizeof(fields) / sizeof(fields[0]); index++) {
        size_t name = strlen(fields[index].name);

        if (length > name && comment[name] == '=' && strncasecmp(comment, fields[index].name, name) == 0) {
            char *target = (char *)title + fields[index].offset;

            if (target[0] == '\0') {
                copy_comment(target, comment + name + 1, length - name - 1);
            }

            return;
        }
    }
}

int opus_tags_parse(const unsigned char *packet, size_t length, OpusTitle *title) {
    memset(title, 0, sizeof(*title));

    if (length < 16 || memcmp(packet, "OpusTags", 8) != 0) {
        return 0;
    }

    size_t position = 8;
    uint32_t vendor = read32(packet + position);

    position += 4;

    if (vendor > length - position || length - position - vendor < 4) {
        return 1;
    }

    position += vendor;

    uint32_t count = read32(packet + position);

    position += 4;

    for (uint32_t index = 0; index < count && length - position >= 4; index++) {
        uint32_t size = read32(packet + position);

        position += 4;

        if (size > length - position) {
            break;
        }

        take_comment(title, (const char *)packet + position, size);

        position += size;
    }

    return 1;
}

static int put_text(unsigned char *out, size_t capacity, size_t *position, const char *prefix, const char *text) {
    size_t prefix_length = strlen(prefix);
    size_t text_length = strlen(text);
    size_t total = prefix_length + text_length;

    if (*position + 4 + total > capacity) {
        return 0;
    }

    write32(out + *position, (uint32_t)total);
    memcpy(out + *position + 4, prefix, prefix_length);
    memcpy(out + *position + 4 + prefix_length, text, text_length);

    *position += 4 + total;

    return 1;
}

size_t opus_tags_build(unsigned char *out, size_t capacity, const char *vendor, const OpusTitle *title) {
    const char *values[3] = { title ? title->title : "", title ? title->artist : "", title ? title->album : "" };
    const char *names[3] = { "TITLE=", "ARTIST=", "ALBUM=" };
    uint32_t count = 0;
    size_t position = 8;

    if (capacity < 8) {
        return 0;
    }

    memcpy(out, "OpusTags", 8);

    if (!put_text(out, capacity, &position, "", vendor) || position + 4 > capacity) {
        return 0;
    }

    size_t count_at = position;

    position += 4;

    for (int index = 0; index < 3; index++) {
        if (values[index][0] != '\0') {
            if (!put_text(out, capacity, &position, names[index], values[index])) {
                return 0;
            }

            count++;
        }
    }

    write32(out + count_at, count);

    return position;
}

void opus_splicer_init(
    OpusSplicer *splicer, uint32_t serial, SplicePageFn emit, SpliceTitleFn titled, SpliceResetFn reset, void *context) {
    memset(splicer, 0, sizeof(*splicer));

    splicer->serial = serial;
    splicer->sequence = 2;
    splicer->state = SPLICE_IDLE;
    splicer->emit = emit;
    splicer->titled = titled;
    splicer->reset = reset;
    splicer->context = context;
}

void opus_splicer_packets(OpusSplicer *splicer, SplicePacketFn packet_out) {
    splicer->packet_out = packet_out;
}

void opus_splicer_source_ended(OpusSplicer *splicer) {
    splicer->state = SPLICE_IDLE;
    splicer->in_packet = 0;
}

static void splice_begin(OpusSplicer *splicer, const OggPage *page) {
    OpusHead head;
    size_t first = 0;
    int complete = 0;

    for (int index = 0; index < page->segments; index++) {
        first += page->lacing[index];

        if (page->lacing[index] < 255) {
            complete = 1;

            break;
        }
    }

    if (!complete || first > OPUS_HEAD_MAX || !opus_head_parse(page->body, first, &head)) {
        if (splicer->state == SPLICE_AUDIO) {
            splicer->state = SPLICE_IDLE;
        }

        splicer->skipped_pages++;

        return;
    }

    if (splicer->have_head && !opus_head_compatible(&splicer->head, &head)) {
        splicer->resets++;

        if (splicer->reset) {
            splicer->reset(splicer->context);
        }
    }

    splicer->head = head;

    memcpy(splicer->head_packet, page->body, first);

    splicer->head_length = first;
    splicer->have_head = 1;
    splicer->chain_serial = page->serial;
    splicer->state = SPLICE_TAGS;
    splicer->tags_length = 0;
    splicer->in_packet = 0;
    splicer->chains++;
}

static void splice_tags(OpusSplicer *splicer, const OggPage *page) {
    size_t offset = 0;

    for (int index = 0; index < page->segments; index++) {
        size_t size = page->lacing[index];
        size_t room = sizeof(splicer->tags) - splicer->tags_length;
        size_t take = size < room ? size : room;

        memcpy(splicer->tags + splicer->tags_length, page->body + offset, take);

        splicer->tags_length += take;
        offset += size;

        if (size < 255) {
            OpusTitle title;

            if (opus_tags_parse(splicer->tags, splicer->tags_length, &title) && splicer->titled) {
                splicer->titled(splicer->context, &title);
            }

            splicer->state = SPLICE_AUDIO;
            splicer->in_packet = 0;

            return;
        }
    }
}

static void splice_audio(OpusSplicer *splicer, const OggPage *page) {
    int continued = (page->flags & OGG_CONTINUED) != 0;
    int in_packet = continued;
    int known = continued ? splicer->in_packet && splicer->packet_known : 1;
    unsigned char first[2] = { 0, 0 };
    size_t first_length = 0;
    size_t offset = 0;
    uint64_t clock = splicer->granule;
    uint64_t samples = 0;
    int completed = 0;

    if (in_packet && known) {
        memcpy(first, splicer->packet_start, sizeof(first));

        first_length = splicer->packet_start_length;
    }
    else {
        splicer->packet_length = 0;
        splicer->packet_overflow = 0;
    }

    for (int index = 0; index < page->segments; index++) {
        size_t size = page->lacing[index];

        if (!in_packet) {
            in_packet = 1;
            known = 1;
            first_length = size < 2 ? size : 2;
            splicer->packet_length = 0;
            splicer->packet_overflow = 0;

            memcpy(first, page->body + offset, first_length);
        }

        if (known) {
            if (splicer->packet_length + size <= sizeof(splicer->packet)) {
                memcpy(splicer->packet + splicer->packet_length, page->body + offset, size);

                splicer->packet_length += size;
            }
            else {
                splicer->packet_overflow = 1;
            }
        }

        offset += size;

        if (size < 255) {
            int count = known ? opus_packet_samples(first, first_length) : -1;

            if (count > 0) {
                if (splicer->packet_overflow) {
                    splicer->oversized_packets++;
                }
                else if (splicer->packet_out) {
                    splicer->packet_out(splicer->context, splicer->packet, splicer->packet_length, clock, count);
                }

                clock += (uint64_t)count;
                samples += (uint64_t)count;
            }

            completed++;
            in_packet = 0;
            splicer->packet_length = 0;
            splicer->packet_overflow = 0;
        }
    }

    splicer->in_packet = in_packet;
    splicer->packet_known = known;

    if (in_packet) {
        memcpy(splicer->packet_start, first, sizeof(first));

        splicer->packet_start_length = first_length;
    }

    splicer->granule = clock;
    splicer->packets += (uint64_t)completed;

    memcpy(splicer->page, page->bytes, page->length);

    ogg_page_rewrite(
        splicer->page, page->length, page->flags & OGG_CONTINUED, completed > 0 ? splicer->granule : OGG_NO_GRANULE,
        splicer->serial, splicer->sequence++);

    splicer->pages_out++;

    if (page->flags & OGG_EOS) {
        splicer->state = SPLICE_IDLE;
    }

    splicer->emit(splicer->context, splicer->page, page->length, samples);
}

void opus_splicer_page(OpusSplicer *splicer, const OggPage *page) {
    splicer->pages_in++;

    if (page->flags & OGG_BOS) {
        splice_begin(splicer, page);

        return;
    }

    if (splicer->state == SPLICE_IDLE || page->serial != splicer->chain_serial) {
        splicer->skipped_pages++;

        return;
    }

    if (splicer->state == SPLICE_TAGS) {
        splice_tags(splicer, page);

        return;
    }

    splice_audio(splicer, page);
}

size_t opus_splicer_headers(const OpusSplicer *splicer, const OpusTitle *title, unsigned char *out, size_t capacity) {
    unsigned char tags[64 + 3 * (OPUS_TEXT + 16)];

    if (!splicer->have_head) {
        return 0;
    }

    size_t tags_length = opus_tags_build(tags, sizeof(tags), "caligo-relay", title);
    size_t first = ogg_page_build(out, capacity, OGG_BOS, 0, splicer->serial, 0, splicer->head_packet, splicer->head_length);

    if (tags_length == 0 || first == 0) {
        return 0;
    }

    size_t second = ogg_page_build(out + first, capacity - first, 0, 0, splicer->serial, 1, tags, tags_length);

    return second == 0 ? 0 : first + second;
}
