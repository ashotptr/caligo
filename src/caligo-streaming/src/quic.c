#define _GNU_SOURCE

#include "quic.h"

#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#include <openssl/bn.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>

#include "h3zero.h"
#include "h3zero_common.h"
#include "pico_webtransport.h"
#include "picoquic.h"
#include "picoquic_bbr.h"
#include "picosocks.h"

#define RECEIVE_BUFFER 4096
#define RECEIVE_BATCH 256
#define SEND_BATCH 512
#define SOCKET_BUFFER (2 * 1024 * 1024)
#define CERTIFICATE_DAYS 10
#define CERTIFICATE_RENEW_MS (5LL * 24 * 3600 * 1000)
#define CERTIFICATE_RETRY_MS (3600LL * 1000)
#define WT_ERROR_FIRST 0x52e4a40fa8dbULL

typedef struct QuicSession {
    struct QuicSession *next;
    struct QuicSession *previous;
    picoquic_cnx_t *cnx;
    h3zero_callback_ctx_t *h3;
    uint64_t control;
    MoqlSession *moq;
    MoqlTransport transport;
    picowt_capsule_t capsule;
    int bare;
    int ending;
    int failed;
    int close_sent;
    char reason[96];
} QuicSession;

static picoquic_quic_t *quic;
static int quic_fd = -1;
static uint16_t quic_port_network;
static QuicSession *sessions;
static MoqlSource *lite;
static uint64_t lite_hop;
static int session_limit;
static QuicStats counters;
static char fingerprint[65];
static char folder[64];
static char cert_path[160];
static char key_path[160];
static int generated;
static int64_t renew_at_ms;
static int64_t expires_wall_ms;
static picohttp_server_path_item_t path_item;
static picohttp_server_parameters_t server_parameters;

static void say(const char *format, ...) {
    char text[512];
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);

    fprintf(stderr, "[relay] quic: %s\n", text);
}

static int64_t now_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int64_t wall_ms(void) {
    struct timespec now;

    clock_gettime(CLOCK_REALTIME, &now);

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static uint64_t wt_code(uint64_t code) {
    return WT_ERROR_FIRST + code + code / 0x1e;
}

static int remember_certificate(X509 *cert, char *hex, int64_t *expires_ms) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    struct tm expires;

    if (!X509_digest(cert, EVP_sha256(), digest, &length) || length != 32) {
        return 0;
    }

    for (unsigned int index = 0; index < length; index++) {
        snprintf(hex + index * 2, 3, "%02x", digest[index]);
    }

    *expires_ms = ASN1_TIME_to_tm(X509_get0_notAfter(cert), &expires) ? (int64_t)timegm(&expires) * 1000 : 0;

    return 1;
}

static int write_pem(const char *path, X509 *cert, EVP_PKEY *key) {
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    FILE *file = fd >= 0 ? fdopen(fd, "w") : NULL;

    if (!file) {
        if (fd >= 0) {
            close(fd);
        }

        return 0;
    }

    int written = cert ? PEM_write_X509(file, cert) : PEM_write_PrivateKey(file, key, NULL, NULL, 0, NULL, NULL);

    return fclose(file) == 0 && written;
}

static int make_certificate(char *hex, int64_t *expires_ms) {
    EVP_PKEY *key = EVP_EC_gen("P-256");
    X509 *cert = X509_new();
    BIGNUM *serial = BN_new();
    X509_NAME *name = NULL;
    X509_EXTENSION *names = NULL;
    X509V3_CTX context;
    int ok = key && cert && serial;

    ok = ok && X509_set_version(cert, 2) && BN_rand(serial, 63, BN_RAND_TOP_ANY, BN_RAND_BOTTOM_ANY) &&
        BN_to_ASN1_INTEGER(serial, X509_get_serialNumber(cert)) != NULL &&
        X509_gmtime_adj(X509_getm_notBefore(cert), -3600) != NULL &&
        X509_gmtime_adj(X509_getm_notAfter(cert), (long)CERTIFICATE_DAYS * 86400) != NULL &&
        X509_set_pubkey(cert, key);

    if (ok) {
        name = X509_get_subject_name(cert);
        ok = X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, (const unsigned char *)"caligo-relay", -1, -1, 0) &&
            X509_set_issuer_name(cert, name);
    }

    if (ok) {
        X509V3_set_ctx_nodb(&context);
        X509V3_set_ctx(&context, cert, cert, NULL, NULL, 0);

        names = X509V3_EXT_conf_nid(NULL, &context, NID_subject_alt_name, "DNS:localhost,IP:127.0.0.1");
        ok = names && X509_add_ext(cert, names, -1) && X509_sign(cert, key, EVP_sha256()) > 0;
    }

    ok = ok && write_pem(cert_path, cert, NULL) && write_pem(key_path, NULL, key) && remember_certificate(cert, hex, expires_ms);

    X509_EXTENSION_free(names);
    BN_free(serial);
    X509_free(cert);
    EVP_PKEY_free(key);

    return ok;
}

static int read_certificate(const char *path, char *hex, int64_t *expires_ms) {
    FILE *file = fopen(path, "r");
    X509 *cert = file ? PEM_read_X509(file, NULL, NULL, NULL) : NULL;
    int ok = cert && remember_certificate(cert, hex, expires_ms);

    X509_free(cert);

    if (file) {
        fclose(file);
    }

    return ok;
}

static int on_webtransport(picoquic_cnx_t *cnx, uint8_t *bytes, size_t length, picohttp_call_back_event_t event,
                           h3zero_stream_ctx_t *stream_ctx, void *context);

static uint64_t open_uni(void *context) {
    QuicSession *session = context;
    h3zero_stream_ctx_t *stream = picowt_create_local_stream(session->cnx, 0, session->h3, session->control);

    if (!stream) {
        return MOQL_NO_STREAM;
    }

    stream->path_callback = on_webtransport;
    stream->path_callback_ctx = session;

    return stream->stream_id;
}

static void wake(void *context, uint64_t stream) {
    QuicSession *session = context;
    h3zero_stream_ctx_t *stream_ctx = h3zero_find_stream(session->h3, stream);

    if (stream_ctx) {
        picoquic_mark_active_stream(session->cnx, stream, 1, stream_ctx);
    }
}

static void reset(void *context, uint64_t stream, uint64_t code) {
    QuicSession *session = context;
    h3zero_stream_ctx_t *stream_ctx = h3zero_find_stream(session->h3, stream);

    if (stream_ctx) {
        picowt_reset_stream(session->cnx, stream_ctx, wt_code(code));
    }
    else {
        picoquic_reset_stream(session->cnx, stream, wt_code(code));
    }
}

static void stop(void *context, uint64_t stream, uint64_t code) {
    QuicSession *session = context;

    picoquic_stop_sending(session->cnx, stream, wt_code(code));
}

typedef struct {
    void *context;
    h3zero_stream_ctx_t *stream;
} Provide;

static unsigned char *provide_buffer(void *context, size_t length, int fin, int more) {
    Provide *provide = context;

    if (fin) {
        provide->stream->ps.stream_state.is_fin_sent = 1;
    }

    return picoquic_provide_stream_data_buffer(provide->context, length, fin, more);
}

static void add_totals(const MoqlSession *moq) {
    counters.groups_out += moq->groups_out;
    counters.frames_out += moq->frames_out;
    counters.skips += moq->skips;
}

static void release_session(QuicSession *session, h3zero_stream_ctx_t *control) {
    if (control) {
        picowt_deregister(session->cnx, session->h3, control);
    }

    picowt_release_capsule(&session->capsule);

    if (session->previous) {
        session->previous->next = session->next;
    }
    else {
        sessions = session->next;
    }

    if (session->next) {
        session->next->previous = session->previous;
    }

    add_totals(session->moq);
    moql_free(session->moq);
    free(session);

    counters.sessions--;
    counters.closed++;
}

static void end_session(QuicSession *session) {
    h3zero_stream_ctx_t *control = h3zero_find_stream(session->h3, session->control);

    if (control && session->failed && !control->ps.stream_state.is_fin_sent) {
        picowt_send_close_session_message(session->cnx, control, 1, session->reason);
    }

    h3zero_delete_stream_prefix(session->cnx, session->h3, session->control);

    if (!control) {
        release_session(session, NULL);
    }
}

static void fail_session(QuicSession *session, const char *reason) {
    session->ending = 1;
    session->failed = 1;

    snprintf(session->reason, sizeof(session->reason), "%s", reason ? reason : "protocol error");

    counters.failures++;
}

static uint64_t bare_open_uni(void *context) {
    QuicSession *session = context;
    uint64_t stream = picoquic_get_next_local_stream_id(session->cnx, 1);

    return picoquic_set_app_stream_ctx(session->cnx, stream, session) == 0 ? stream : MOQL_NO_STREAM;
}

static void bare_wake(void *context, uint64_t stream) {
    QuicSession *session = context;

    picoquic_mark_active_stream(session->cnx, stream, 1, session);
}

static void bare_reset(void *context, uint64_t stream, uint64_t code) {
    QuicSession *session = context;

    picoquic_reset_stream(session->cnx, stream, code);
}

static void bare_stop(void *context, uint64_t stream, uint64_t code) {
    QuicSession *session = context;

    picoquic_stop_sending(session->cnx, stream, code);
}

static unsigned char *bare_buffer(void *context, size_t length, int fin, int more) {
    return picoquic_provide_stream_data_buffer(context, length, fin, more);
}

static QuicSession *create_session(picoquic_cnx_t *cnx, int bare) {
    QuicSession *session = calloc(1, sizeof(QuicSession));

    if (!session) {
        return NULL;
    }

    session->cnx = cnx;
    session->bare = bare;
    session->transport.context = session;
    session->transport.open_uni = bare ? bare_open_uni : open_uni;
    session->transport.wake = bare ? bare_wake : wake;
    session->transport.reset = bare ? bare_reset : reset;
    session->transport.stop = bare ? bare_stop : stop;
    session->moq = moql_create_native(lite, 0, lite_hop, &session->transport, now_ms());

    if (!session->moq) {
        free(session);

        return NULL;
    }

    return session;
}

static void link_session(QuicSession *session) {
    session->next = sessions;

    if (sessions) {
        sessions->previous = session;
    }

    sessions = session;

    counters.accepted++;
    counters.sessions++;

    if (session->bare) {
        counters.bare_accepted++;
    }

    if (counters.sessions > counters.peak) {
        counters.peak = counters.sessions;
    }
}

static int accept_session(picoquic_cnx_t *cnx, h3zero_stream_ctx_t *stream_ctx) {
    h3zero_callback_ctx_t *h3 = picoquic_get_callback_context(cnx);
    QuicSession *session = NULL;

    if (!h3 || !picowt_is_webtransport_connect(stream_ctx) || picowt_select_wt_protocol(stream_ctx, QUIC_PROTOCOL) != 0 ||
        counters.sessions >= session_limit || (session = create_session(cnx, 0)) == NULL) {
        counters.refused++;

        return -1;
    }

    session->h3 = h3;
    session->control = stream_ctx->stream_id;

    if (h3zero_declare_stream_prefix(h3, session->control, on_webtransport, session) != 0) {
        moql_free(session->moq);
        free(session);

        counters.refused++;

        return -1;
    }

    stream_ctx->ps.stream_state.control_stream_id = session->control;
    stream_ctx->path_callback = on_webtransport;
    stream_ctx->path_callback_ctx = session;

    link_session(session);

    return 0;
}

static int on_bare(picoquic_cnx_t *cnx, uint64_t stream, uint8_t *bytes, size_t length, picoquic_call_back_event_t event,
                   void *callback_ctx, void *stream_ctx) {
    QuicSession *session = callback_ctx;

    (void)stream_ctx;

    if (event == picoquic_callback_close || event == picoquic_callback_application_close ||
        event == picoquic_callback_stateless_reset) {
        if (session) {
            picoquic_set_callback(cnx, NULL, NULL);
            release_session(session, NULL);
        }

        return 0;
    }

    if (!session) {
        session = counters.sessions < session_limit ? create_session(cnx, 1) : NULL;

        if (!session) {
            counters.refused++;
            picoquic_set_callback(cnx, NULL, NULL);

            return picoquic_close(cnx, MOQL_ERROR_CANCELLED);
        }

        picoquic_set_callback(cnx, on_bare, session);
        link_session(session);
    }

    switch (event) {
        case picoquic_callback_stream_data:
        case picoquic_callback_stream_fin:
            if (!session->ending && moql_stream_data(session->moq, stream, bytes, length, event == picoquic_callback_stream_fin) < 0) {
                fail_session(session, session->moq->error);
            }

            return 0;

        case picoquic_callback_prepare_to_send:
            if (session->ending) {
                picoquic_provide_stream_data_buffer(bytes, 0, 0, 0);

                return 0;
            }

            return moql_stream_send(session->moq, stream, length, bare_buffer, bytes) < 0 ? -1 : 0;

        case picoquic_callback_stream_reset:
            moql_stream_reset(session->moq, stream);

            return 0;

        case picoquic_callback_stop_sending:
            moql_stream_stop(session->moq, stream, MOQL_ERROR_CANCELLED);

            return 0;

        default:
            return 0;
    }
}

static int offered(const picoquic_iovec_t *item, const char *name) {
    size_t length = strlen(name);

    return item->len == length && memcmp(item->base, name, length) == 0;
}

static size_t select_alpn(picoquic_quic_t *context, picoquic_iovec_t *list, size_t count) {
    picoquic_cnx_t *cnx = picoquic_get_cnx_in_progress(context);

    for (size_t index = 0; index < count; index++) {
        if (offered(&list[index], "h3")) {
            if (cnx) {
                picoquic_set_callback(cnx, h3zero_callback, &server_parameters);
            }

            return index;
        }

        if (offered(&list[index], QUIC_PROTOCOL) && counters.sessions < session_limit) {
            if (cnx) {
                picoquic_set_callback(cnx, on_bare, NULL);
            }

            return index;
        }
    }

    return count;
}

static void session_data(QuicSession *session, h3zero_stream_ctx_t *stream_ctx, uint8_t *bytes, size_t length, int fin) {
    if (session->ending) {
        return;
    }

    if (stream_ctx->stream_id == session->control) {
        if (length > 0 && picowt_receive_capsule(session->cnx, bytes, bytes + length, &session->capsule) != 0) {
            fail_session(session, "malformed capsule");

            return;
        }

        if (fin || (session->capsule.h3_capsule.is_stored &&
                    session->capsule.h3_capsule.capsule_type == picowt_capsule_close_webtransport_session)) {
            session->ending = 1;
        }

        return;
    }

    if (moql_stream_data(session->moq, stream_ctx->stream_id, bytes, length, fin) < 0) {
        fail_session(session, session->moq->error);
    }
}

static int on_webtransport(picoquic_cnx_t *cnx, uint8_t *bytes, size_t length, picohttp_call_back_event_t event,
                           h3zero_stream_ctx_t *stream_ctx, void *context) {
    QuicSession *session = context;

    switch (event) {
        case picohttp_callback_connect:
            return accept_session(cnx, stream_ctx);

        case picohttp_callback_post_data:
        case picohttp_callback_post_fin:
            if (!session) {
                return -1;
            }

            session_data(session, stream_ctx, bytes, length, event == picohttp_callback_post_fin);

            return 0;

        case picohttp_callback_provide_data: {
            Provide provide = { bytes, stream_ctx };

            if (!session || session->ending) {
                picoquic_provide_stream_data_buffer(bytes, 0, 0, 0);

                return 0;
            }

            return moql_stream_send(session->moq, stream_ctx->stream_id, length, provide_buffer, &provide) < 0 ? -1 : 0;
        }

        case picohttp_callback_reset:
        case picohttp_callback_stop_sending:
            if (!session) {
                return 0;
            }

            if (stream_ctx->stream_id == session->control) {
                session->ending = 1;
            }
            else if (event == picohttp_callback_reset) {
                moql_stream_reset(session->moq, stream_ctx->stream_id);
            }
            else {
                moql_stream_stop(session->moq, stream_ctx->stream_id, MOQL_ERROR_CANCELLED);
            }

            return 0;

        case picohttp_callback_deregister:
            if (session) {
                release_session(session, stream_ctx);
            }

            return 0;

        case picohttp_callback_free:
            if (session && stream_ctx->stream_id == session->control) {
                stream_ctx->ps.stream_state.is_fin_sent = 1;

                h3zero_delete_stream_prefix(cnx, session->h3, session->control);
            }

            return 0;

        default:
            return 0;
    }
}

static int open_socket(const QuicConfig *config) {
    struct addrinfo hints;
    struct addrinfo *found = NULL;
    char port[16];
    int one = 1;
    int size = SOCKET_BUFFER;
    int ecn_receive = 0;
    int ecn_send = 0;

    memset(&hints, 0, sizeof(hints));

    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST;

    snprintf(port, sizeof(port), "%d", config->port);

    if (getaddrinfo(config->host, port, &hints, &found) != 0 || !found) {
        say("cannot use %s:%d", config->host, config->port);

        return -1;
    }

    int fd = socket(found->ai_family, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);

    if (fd < 0) {
        freeaddrinfo(found);

        return -1;
    }

    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &size, sizeof(size));
    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &size, sizeof(size));
    picoquic_socket_set_pkt_info(fd, found->ai_family);
    picoquic_socket_set_ecn_options(fd, found->ai_family, &ecn_receive, &ecn_send);

    if (bind(fd, found->ai_addr, found->ai_addrlen) < 0) {
        say("cannot bind udp %s:%d: %s", config->host, config->port, strerror(errno));
        close(fd);
        freeaddrinfo(found);

        return -1;
    }

    freeaddrinfo(found);

    struct epoll_event event;

    memset(&event, 0, sizeof(event));

    event.events = EPOLLIN;
    event.data.ptr = config->epoll_tag;

    if (epoll_ctl(config->epoll_fd, EPOLL_CTL_ADD, fd, &event) < 0) {
        close(fd);

        return -1;
    }

    return fd;
}

int quic_start(const QuicConfig *config, MoqlSource *source, uint64_t hop) {
    char hex[65];
    int64_t expires = 0;

    if (config->port <= 0) {
        say("switched off");

        return 0;
    }

    lite = source;
    lite_hop = hop;
    session_limit = config->max_sessions;
    counters.port = config->port;
    quic_port_network = htons((uint16_t)config->port);

    if (config->cert_file && *config->cert_file && config->key_file && *config->key_file) {
        snprintf(cert_path, sizeof(cert_path), "%s", config->cert_file);
        snprintf(key_path, sizeof(key_path), "%s", config->key_file);

        if (!read_certificate(cert_path, hex, &expires)) {
            say("cannot read the certificate in %s", cert_path);

            return -1;
        }
    }
    else {
        snprintf(folder, sizeof(folder), "/tmp/caligo-quic-XXXXXX");

        if (!mkdtemp(folder)) {
            say("cannot create a private folder for the certificate: %s", strerror(errno));

            return -1;
        }

        snprintf(cert_path, sizeof(cert_path), "%s/cert.pem", folder);
        snprintf(key_path, sizeof(key_path), "%s/key.pem", folder);

        if (!make_certificate(hex, &expires)) {
            say("cannot generate a certificate");

            return -1;
        }

        generated = 1;
        renew_at_ms = now_ms() + CERTIFICATE_RENEW_MS;
    }

    memcpy(fingerprint, hex, sizeof(fingerprint));

    expires_wall_ms = expires;

    path_item.path = QUIC_PATH;
    path_item.path_length = strlen(QUIC_PATH);
    path_item.path_callback = on_webtransport;
    path_item.connect_protocol = H3ZERO_WEBTRANSPORT_H3_PROTOCOL;
    path_item.connect_protocol_length = strlen(H3ZERO_WEBTRANSPORT_H3_PROTOCOL);
    path_item.origin_validator = h3zero_origin_validator_allow_all;
    server_parameters.path_table = &path_item;
    server_parameters.path_table_nb = 1;

    quic = picoquic_create((uint32_t)config->max_sessions * 2 + 16, cert_path, key_path, NULL, NULL, h3zero_callback,
        &server_parameters, NULL, NULL, NULL, picoquic_current_time(), NULL, NULL, NULL, 0);

    if (!quic) {
        say("cannot create the QUIC context");

        return -1;
    }

    picoquic_set_alpn_select_fn_v2(quic, select_alpn);
    picoquic_set_default_congestion_algorithm(quic, picoquic_bbr_algorithm);
    picoquic_set_default_idle_timeout(quic, (uint64_t)config->idle_seconds * 1000);
    picoquic_set_cookie_mode(quic, 2);
    picowt_set_default_transport_parameters(quic);

    quic_fd = open_socket(config);

    if (quic_fd < 0) {
        picoquic_free(quic);

        quic = NULL;

        return -1;
    }

    counters.enabled = 1;

    say("WebTransport on udp %s:%d%s and bare QUIC with ALPN %s, certificate sha-256 %s%s", config->host, config->port,
        QUIC_PATH, QUIC_PROTOCOL, fingerprint, generated ? " (generated, valid for 10 days, renewed every 5)" : "");

    return 0;
}

void quic_readable(int64_t now) {
    unsigned char buffer[RECEIVE_BUFFER];

    (void)now;

    if (quic_fd < 0) {
        return;
    }

    for (int round = 0; round < RECEIVE_BATCH; round++) {
        struct sockaddr_storage from;
        struct sockaddr_storage to;
        int if_index = 0;
        unsigned char ecn = 0;
        picoquic_cnx_t *first = NULL;

        memset(&from, 0, sizeof(from));
        memset(&to, 0, sizeof(to));

        int length = picoquic_recvmsg(quic_fd, &from, &to, &if_index, &ecn, buffer, (int)sizeof(buffer));

        if (length <= 0) {
            break;
        }

        if (to.ss_family == AF_INET) {
            ((struct sockaddr_in *)&to)->sin_port = quic_port_network;
        }
        else if (to.ss_family == AF_INET6) {
            ((struct sockaddr_in6 *)&to)->sin6_port = quic_port_network;
        }

        counters.packets_in++;

        picoquic_incoming_packet_ex(quic, buffer, (size_t)length, (struct sockaddr *)&from, (struct sockaddr *)&to, if_index,
            ecn, &first, picoquic_current_time());
    }
}

static void renew_certificate(int64_t now) {
    char hex[65];
    int64_t expires = 0;

    if (!generated || now < renew_at_ms) {
        return;
    }

    if (make_certificate(hex, &expires) && picoquic_refresh_tls_certificate(quic, cert_path, key_path) == 0) {
        memcpy(fingerprint, hex, sizeof(fingerprint));

        expires_wall_ms = expires;
        renew_at_ms = now + CERTIFICATE_RENEW_MS;

        say("renewed the certificate, sha-256 %s", fingerprint);
    }
    else {
        renew_at_ms = now + CERTIFICATE_RETRY_MS;

        say("could not renew the certificate, trying again in an hour");
    }
}

void quic_service(int64_t now) {
    if (!quic) {
        return;
    }

    QuicSession *session = sessions;

    while (session) {
        QuicSession *next = session->next;

        if (session->ending && session->bare) {
            if (!session->close_sent) {
                session->close_sent = 1;

                picoquic_close(session->cnx, session->failed ? 1 : 0);
            }
        }
        else if (session->ending) {
            end_session(session);
        }
        else {
            moql_pump(session->moq, now);
        }

        session = next;
    }

    renew_certificate(now);

    uint64_t current = picoquic_current_time();

    for (int round = 0; round < SEND_BATCH; round++) {
        unsigned char packet[PICOQUIC_MAX_PACKET_SIZE];
        size_t length = 0;
        struct sockaddr_storage to;
        struct sockaddr_storage from;
        int if_index = 0;
        int error = 0;
        picoquic_connection_id_t log_cid;
        picoquic_cnx_t *last = NULL;

        memset(&to, 0, sizeof(to));
        memset(&from, 0, sizeof(from));

        if (picoquic_prepare_next_packet(quic, current, packet, sizeof(packet), &length, &to, &from, &if_index, &log_cid,
                &last) != 0 || length == 0) {
            break;
        }

        if (picoquic_sendmsg(quic_fd, (struct sockaddr *)&to, (struct sockaddr *)&from, if_index, (const char *)packet,
                (int)length, 0, &error) > 0) {
            counters.packets_out++;
        }
    }
}

int quic_timeout(int64_t now, int limit) {
    (void)now;

    if (!quic) {
        return limit;
    }

    int64_t delay = picoquic_get_next_wake_delay(quic, picoquic_current_time(), (int64_t)limit * 1000);

    if (delay <= 0) {
        return 0;
    }

    int64_t milliseconds = (delay + 999) / 1000;

    return milliseconds < limit ? (int)milliseconds : limit;
}

const char *quic_fingerprint(void) {
    return quic ? fingerprint : NULL;
}

void quic_stats(QuicStats *stats) {
    *stats = counters;

    for (const QuicSession *session = sessions; session; session = session->next) {
        stats->groups_out += session->moq->groups_out;
        stats->frames_out += session->moq->frames_out;
        stats->skips += session->moq->skips;
    }

    stats->certificate_valid_ms = expires_wall_ms > 0 ? expires_wall_ms - wall_ms() : 0;
}

void quic_stop(void) {
    if (quic) {
        picoquic_free(quic);

        quic = NULL;
    }

    if (quic_fd >= 0) {
        close(quic_fd);

        quic_fd = -1;
    }

    if (generated) {
        unlink(cert_path);
        unlink(key_path);
        rmdir(folder);
    }
}
