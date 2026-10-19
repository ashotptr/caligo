#ifndef RELAY_QMUX_H
#define RELAY_QMUX_H

#include <stddef.h>
#include <stdint.h>

#define QMUX_TRANSPORT_PARAMETERS 0x3f5153300d0a0d0aULL
#define QMUX_PING_REQUEST 0x348c67529ef8c7bdULL
#define QMUX_PING_RESPONSE 0x348c67529ef8c7beULL
#define QMUX_MAX_RECORD_SIZE_ID 0x0571c59429cd0845ULL
#define QMUX_DEFAULT_RECORD 16382

typedef enum {
    QMUX_FRAME_PADDING,
    QMUX_FRAME_RESET_STREAM,
    QMUX_FRAME_STOP_SENDING,
    QMUX_FRAME_STREAM,
    QMUX_FRAME_MAX_DATA,
    QMUX_FRAME_MAX_STREAM_DATA,
    QMUX_FRAME_MAX_STREAMS_BIDI,
    QMUX_FRAME_MAX_STREAMS_UNI,
    QMUX_FRAME_DATA_BLOCKED,
    QMUX_FRAME_STREAM_DATA_BLOCKED,
    QMUX_FRAME_STREAMS_BLOCKED,
    QMUX_FRAME_CONNECTION_CLOSE,
    QMUX_FRAME_PARAMETERS,
    QMUX_FRAME_PING,
    QMUX_FRAME_PONG,
    QMUX_FRAME_DATAGRAM,
    QMUX_FRAME_RESET_STREAM_AT
} QmuxFrameType;

typedef struct {
    QmuxFrameType type;
    uint64_t stream;
    uint64_t offset;
    uint64_t value;
    uint64_t size;
    int fin;
    const unsigned char *data;
    size_t length;
} QmuxFrame;

typedef struct {
    uint64_t max_idle_timeout;
    uint64_t initial_max_data;
    uint64_t initial_max_stream_data_bidi_local;
    uint64_t initial_max_stream_data_bidi_remote;
    uint64_t initial_max_stream_data_uni;
    uint64_t initial_max_streams_bidi;
    uint64_t initial_max_streams_uni;
    uint64_t max_record_size;
} QmuxParams;

long qmux_decode(const unsigned char *data, size_t length, QmuxFrame *frame);

int qmux_params_parse(const unsigned char *data, size_t length, QmuxParams *params);

size_t qmux_params_frame(unsigned char *out, size_t capacity, const QmuxParams *params);

size_t qmux_stream_header(unsigned char *out, uint64_t stream, uint64_t offset, size_t length, int fin);

size_t qmux_reset_stream(unsigned char *out, uint64_t stream, uint64_t code, uint64_t final_size);

size_t qmux_stop_sending(unsigned char *out, uint64_t stream, uint64_t code);

size_t qmux_max_data(unsigned char *out, uint64_t value);

size_t qmux_max_stream_data(unsigned char *out, uint64_t stream, uint64_t value);

size_t qmux_max_streams(unsigned char *out, int bidi, uint64_t value);

size_t qmux_ping(unsigned char *out, int response, uint64_t sequence);

size_t qmux_close(unsigned char *out, size_t capacity, uint64_t code, const char *reason);

#endif
