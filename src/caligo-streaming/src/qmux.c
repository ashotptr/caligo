#include "qmux.h"

#include <string.h>

#include "objects.h"

static int take(const unsigned char *data, size_t length, size_t *position, uint64_t *value) {
    long used = varint_decode(data + *position, length - *position, value);

    if (used <= 0) {
        return 0;
    }

    *position += (size_t)used;

    return 1;
}

long qmux_decode(const unsigned char *data, size_t length, QmuxFrame *frame) {
    size_t position = 0;
    uint64_t type = 0;
    uint64_t skip = 0;

    memset(frame, 0, sizeof(*frame));

    if (!take(data, length, &position, &type)) {
        return -1;
    }

    if (type == 0x00) {
        frame->type = QMUX_FRAME_PADDING;

        return (long)position;
    }

    if (type >= 0x08 && type <= 0x0f) {
        frame->type = QMUX_FRAME_STREAM;
        frame->fin = (type & 0x01) != 0;

        if (!take(data, length, &position, &frame->stream)) {
            return -1;
        }

        if ((type & 0x04) && !take(data, length, &position, &frame->offset)) {
            return -1;
        }

        uint64_t size = length - position;

        if ((type & 0x02) && !take(data, length, &position, &size)) {
            return -1;
        }

        if (size > length - position) {
            return -1;
        }

        frame->data = data + position;
        frame->length = (size_t)size;

        return (long)(position + (size_t)size);
    }

    switch (type) {
        case 0x04:
            frame->type = QMUX_FRAME_RESET_STREAM;

            return take(data, length, &position, &frame->stream) && take(data, length, &position, &frame->value) &&
                take(data, length, &position, &frame->size) ? (long)position : -1;

        case 0x24:
            frame->type = QMUX_FRAME_RESET_STREAM_AT;

            return take(data, length, &position, &frame->stream) && take(data, length, &position, &frame->value) &&
                take(data, length, &position, &frame->size) && take(data, length, &position, &skip) ? (long)position : -1;

        case 0x05:
            frame->type = QMUX_FRAME_STOP_SENDING;

            return take(data, length, &position, &frame->stream) && take(data, length, &position, &frame->value)
                ? (long)position : -1;

        case 0x10:
        case 0x12:
        case 0x13:
        case 0x14:
        case 0x16:
        case 0x17:
            frame->type = type == 0x10 ? QMUX_FRAME_MAX_DATA
                : type == 0x12 ? QMUX_FRAME_MAX_STREAMS_BIDI
                : type == 0x13 ? QMUX_FRAME_MAX_STREAMS_UNI
                : type == 0x14 ? QMUX_FRAME_DATA_BLOCKED
                : QMUX_FRAME_STREAMS_BLOCKED;

            return take(data, length, &position, &frame->value) ? (long)position : -1;

        case 0x11:
        case 0x15:
            frame->type = type == 0x11 ? QMUX_FRAME_MAX_STREAM_DATA : QMUX_FRAME_STREAM_DATA_BLOCKED;

            return take(data, length, &position, &frame->stream) && take(data, length, &position, &frame->value)
                ? (long)position : -1;

        case 0x1c:
        case 0x1d: {
            uint64_t reason = 0;

            frame->type = QMUX_FRAME_CONNECTION_CLOSE;

            if (!take(data, length, &position, &frame->value) ||
                (type == 0x1c && !take(data, length, &position, &skip)) ||
                !take(data, length, &position, &reason) || reason > length - position) {
                return -1;
            }

            frame->data = data + position;
            frame->length = (size_t)reason;

            return (long)(position + (size_t)reason);
        }

        case 0x30:
        case 0x31: {
            uint64_t size = length - position;

            frame->type = QMUX_FRAME_DATAGRAM;

            if (type == 0x31 && !take(data, length, &position, &size)) {
                return -1;
            }

            if (size > length - position) {
                return -1;
            }

            frame->data = data + position;
            frame->length = (size_t)size;

            return (long)(position + (size_t)size);
        }

        case QMUX_TRANSPORT_PARAMETERS: {
            uint64_t size = 0;

            frame->type = QMUX_FRAME_PARAMETERS;

            if (!take(data, length, &position, &size) || size > length - position) {
                return -1;
            }

            frame->data = data + position;
            frame->length = (size_t)size;

            return (long)(position + (size_t)size);
        }

        case QMUX_PING_REQUEST:
        case QMUX_PING_RESPONSE:
            frame->type = type == QMUX_PING_REQUEST ? QMUX_FRAME_PING : QMUX_FRAME_PONG;

            return take(data, length, &position, &frame->value) ? (long)position : -1;

        default:
            return -1;
    }
}

int qmux_params_parse(const unsigned char *data, size_t length, QmuxParams *params) {
    size_t position = 0;

    memset(params, 0, sizeof(*params));

    params->max_record_size = QMUX_DEFAULT_RECORD;

    while (position < length) {
        uint64_t id = 0;
        uint64_t size = 0;
        uint64_t value = 0;

        if (!take(data, length, &position, &id) || !take(data, length, &position, &size) || size > length - position) {
            return -1;
        }

        const unsigned char *field = data + position;
        int integer = id == 0x01 || (id >= 0x04 && id <= 0x09) || id == QMUX_MAX_RECORD_SIZE_ID;

        if (integer) {
            long used = varint_decode(field, (size_t)size, &value);

            if (used <= 0 || (uint64_t)used != size) {
                return -1;
            }
        }
        else if (id == 0x00 || id == 0x02 || id == 0x03 || (id >= 0x0a && id <= 0x10)) {
            return -1;
        }

        switch (id) {
            case 0x01: params->max_idle_timeout = value; break;
            case 0x04: params->initial_max_data = value; break;
            case 0x05: params->initial_max_stream_data_bidi_local = value; break;
            case 0x06: params->initial_max_stream_data_bidi_remote = value; break;
            case 0x07: params->initial_max_stream_data_uni = value; break;
            case 0x08: params->initial_max_streams_bidi = value; break;
            case 0x09: params->initial_max_streams_uni = value; break;
            case QMUX_MAX_RECORD_SIZE_ID:
                if (value < QMUX_DEFAULT_RECORD) {
                    return -1;
                }

                params->max_record_size = value;

                break;
            default: break;
        }

        position += (size_t)size;
    }

    return 0;
}

static size_t put_param(unsigned char *out, uint64_t id, uint64_t value) {
    size_t position = varint_encode(id, out);

    position += varint_encode(varint_size(value), out + position);
    position += varint_encode(value, out + position);

    return position;
}

size_t qmux_params_frame(unsigned char *out, size_t capacity, const QmuxParams *params) {
    unsigned char body[128];
    size_t length = 0;

    length += put_param(body + length, 0x01, params->max_idle_timeout);
    length += put_param(body + length, 0x04, params->initial_max_data);
    length += put_param(body + length, 0x05, params->initial_max_stream_data_bidi_local);
    length += put_param(body + length, 0x06, params->initial_max_stream_data_bidi_remote);
    length += put_param(body + length, 0x07, params->initial_max_stream_data_uni);
    length += put_param(body + length, 0x08, params->initial_max_streams_bidi);
    length += put_param(body + length, 0x09, params->initial_max_streams_uni);
    length += put_param(body + length, QMUX_MAX_RECORD_SIZE_ID, params->max_record_size);

    if (capacity < 8 + 8 + length) {
        return 0;
    }

    size_t position = varint_encode(QMUX_TRANSPORT_PARAMETERS, out);

    position += varint_encode(length, out + position);

    memcpy(out + position, body, length);

    return position + length;
}

size_t qmux_stream_header(unsigned char *out, uint64_t stream, uint64_t offset, size_t length, int fin) {
    size_t position = varint_encode(fin ? 0x0f : 0x0e, out);

    position += varint_encode(stream, out + position);
    position += varint_encode(offset, out + position);
    position += varint_encode(length, out + position);

    return position;
}

size_t qmux_reset_stream(unsigned char *out, uint64_t stream, uint64_t code, uint64_t final_size) {
    size_t position = varint_encode(0x04, out);

    position += varint_encode(stream, out + position);
    position += varint_encode(code, out + position);
    position += varint_encode(final_size, out + position);

    return position;
}

size_t qmux_stop_sending(unsigned char *out, uint64_t stream, uint64_t code) {
    size_t position = varint_encode(0x05, out);

    position += varint_encode(stream, out + position);
    position += varint_encode(code, out + position);

    return position;
}

size_t qmux_max_data(unsigned char *out, uint64_t value) {
    size_t position = varint_encode(0x10, out);

    return position + varint_encode(value, out + position);
}

size_t qmux_max_stream_data(unsigned char *out, uint64_t stream, uint64_t value) {
    size_t position = varint_encode(0x11, out);

    position += varint_encode(stream, out + position);

    return position + varint_encode(value, out + position);
}

size_t qmux_max_streams(unsigned char *out, int bidi, uint64_t value) {
    size_t position = varint_encode(bidi ? 0x12 : 0x13, out);

    return position + varint_encode(value, out + position);
}

size_t qmux_ping(unsigned char *out, int response, uint64_t sequence) {
    size_t position = varint_encode(response ? QMUX_PING_RESPONSE : QMUX_PING_REQUEST, out);

    return position + varint_encode(sequence, out + position);
}

size_t qmux_close(unsigned char *out, size_t capacity, uint64_t code, const char *reason) {
    size_t reason_length = strlen(reason);

    if (capacity < 1 + 8 + 8 + reason_length) {
        return 0;
    }

    size_t position = varint_encode(0x1d, out);

    position += varint_encode(code, out + position);
    position += varint_encode(reason_length, out + position);

    memcpy(out + position, reason, reason_length);

    return position + reason_length;
}
