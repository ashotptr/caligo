#define _GNU_SOURCE

#include "impair.h"

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <ifaddrs.h>
#include <linux/if_tun.h>
#include <net/if.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/random.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define PACKET_MAX 65536
#define HELD_MAX_BYTES (64 * 1024 * 1024)
#define READ_BATCH 256
#define CONTROL_MS 250
#define STATS_MS 1000
#define MARK "0x4c41"
#define MARK_VALUE 0x4c41
#define TABLE "4242"
#define PATH_LENGTH 512
#define FLOW_SLOTS 4096
#define FLOW_PROBES 16

typedef struct {
    int64_t release_us;
    uint64_t order;
    size_t length;
    unsigned char data[];
} Held;

typedef struct {
    int used;
    uint32_t source;
    uint32_t target;
    uint16_t source_port;
    uint16_t target_port;
    uint8_t protocol;
    int64_t seen_us;
    Impair impair;
} Flow;

typedef struct {
    char tun[IFNAMSIZ];
    char subnet[64];
    char control[256];
    char name[64];
    char address[INET_ADDRSTRLEN];
    char device[IFNAMSIZ];
    int mtu;
} Settings;

static Settings settings;
static Impair impair;
static ImpairProfile current;
static uint64_t seed_base = 1;
static char profile_text[IMPAIR_TEXT_MAX];
static Flow flows[FLOW_SLOTS];
static Held **heap;
static size_t heap_count;
static size_t heap_capacity;
static uint64_t held_order;
static size_t held_bytes;
static uint64_t total_packets;
static uint64_t total_bytes;
static uint64_t total_lost;
static uint64_t total_overflowed;
static uint64_t active_flows;
static uint64_t held_dropped;
static uint64_t sent;
static uint64_t send_errors;
static volatile sig_atomic_t running = 1;

static void say(const char *format, ...) {
    char text[512];
    va_list arguments;

    va_start(arguments, format);
    vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);

    fprintf(stderr, "[netem %s] %s\n", settings.name, text);
}

static int64_t now_us(void) {
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);

    return (int64_t)now.tv_sec * 1000000 + now.tv_nsec / 1000;
}

static void on_signal(int number) {
    (void)number;

    running = 0;
}

static int run(const char *const arguments[], int quiet) {
    pid_t child = fork();

    if (child < 0) {
        return -1;
    }

    if (child == 0) {
        if (quiet) {
            int null = open("/dev/null", O_WRONLY);

            if (null >= 0) {
                dup2(null, STDOUT_FILENO);
                dup2(null, STDERR_FILENO);
            }
        }

        execvp(arguments[0], (char *const *)arguments);
        _exit(127);
    }

    int status = 0;

    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {
    }

    return WIFEXITED(status) ? WEXITSTATUS(status) : -1;
}

static int parse_subnet(const char *text, struct in_addr *network, uint32_t *mask) {
    char copy[64];
    char *slash = NULL;
    long bits = 0;

    snprintf(copy, sizeof(copy), "%s", text);

    slash = strchr(copy, '/');

    if (!slash) {
        return -1;
    }

    *slash = '\0';
    bits = strtol(slash + 1, NULL, 10);

    if (bits < 1 || bits > 32 || inet_pton(AF_INET, copy, network) != 1) {
        return -1;
    }

    *mask = bits == 32 ? 0xFFFFFFFFu : ~(0xFFFFFFFFu >> bits);

    return 0;
}

static int find_address(void) {
    struct in_addr network;
    uint32_t mask = 0;
    struct ifaddrs *list = NULL;
    int found = 0;

    if (parse_subnet(settings.subnet, &network, &mask) < 0 || getifaddrs(&list) < 0) {
        return -1;
    }

    for (struct ifaddrs *item = list; item && !found; item = item->ifa_next) {
        if (!item->ifa_addr || item->ifa_addr->sa_family != AF_INET) {
            continue;
        }

        struct in_addr address = ((struct sockaddr_in *)item->ifa_addr)->sin_addr;

        if ((ntohl(address.s_addr) & mask) == (ntohl(network.s_addr) & mask)) {
            inet_ntop(AF_INET, &address, settings.address, sizeof(settings.address));
            snprintf(settings.device, sizeof(settings.device), "%s", item->ifa_name);

            found = 1;
        }
    }

    freeifaddrs(list);

    if (!found) {
        return -1;
    }

    struct ifreq request;
    int probe = socket(AF_INET, SOCK_DGRAM, 0);

    memset(&request, 0, sizeof(request));
    snprintf(request.ifr_name, sizeof(request.ifr_name), "%s", settings.device);

    settings.mtu = probe >= 0 && ioctl(probe, SIOCGIFMTU, &request) == 0 ? request.ifr_mtu : 1500;

    if (probe >= 0) {
        close(probe);
    }

    return 0;
}

static int open_tun(void) {
    struct ifreq request;
    int fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK | O_CLOEXEC);

    if (fd < 0) {
        say("cannot open /dev/net/tun: %s", strerror(errno));

        return -1;
    }

    memset(&request, 0, sizeof(request));

    request.ifr_flags = IFF_TUN | IFF_NO_PI;

    snprintf(request.ifr_name, sizeof(request.ifr_name), "%s", settings.tun);

    if (ioctl(fd, TUNSETIFF, &request) < 0) {
        say("cannot create %s: %s", settings.tun, strerror(errno));
        close(fd);

        return -1;
    }

    return fd;
}

static int route_through_tun(void) {
    char mtu[16];

    snprintf(mtu, sizeof(mtu), "%d", settings.mtu);

    const char *const link[] = { "ip", "link", "set", "dev", settings.tun, "up", "mtu", mtu, NULL };
    const char *const route[] = { "ip", "route", "replace", settings.subnet, "dev", settings.tun, "src", settings.address,
        "table", TABLE, NULL };
    const char *const drop_mark[] = { "ip", "rule", "del", "fwmark", MARK, "lookup", "main", "priority", "50", NULL };
    const char *const drop_to[] = { "ip", "rule", "del", "to", settings.subnet, "lookup", TABLE, "priority", "60", NULL };
    const char *const add_mark[] = { "ip", "rule", "add", "fwmark", MARK, "lookup", "main", "priority", "50", NULL };
    const char *const add_to[] = { "ip", "rule", "add", "to", settings.subnet, "lookup", TABLE, "priority", "60", NULL };

    while (run(drop_mark, 1) == 0) {
    }

    while (run(drop_to, 1) == 0) {
    }

    if (run(link, 0) != 0 || run(route, 0) != 0 || run(add_mark, 0) != 0 || run(add_to, 0) != 0) {
        say("cannot route %s through %s", settings.subnet, settings.tun);

        return -1;
    }

    return 0;
}

static int open_sender(void) {
    int fd = socket(AF_INET, SOCK_RAW | SOCK_CLOEXEC, IPPROTO_RAW);
    int mark = MARK_VALUE;
    int size = 4 * 1024 * 1024;

    if (fd < 0 || setsockopt(fd, SOL_SOCKET, SO_MARK, &mark, sizeof(mark)) < 0) {
        say("cannot open the raw sender: %s", strerror(errno));

        if (fd >= 0) {
            close(fd);
        }

        return -1;
    }

    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &size, sizeof(size));

    return fd;
}

static void write_file(const char *path, const char *text) {
    char temporary[PATH_LENGTH + 8];

    snprintf(temporary, sizeof(temporary), "%s.tmp", path);

    FILE *file = fopen(temporary, "w");

    if (!file) {
        return;
    }

    fputs(text, file);

    if (fclose(file) == 0) {
        rename(temporary, path);
    }
}

static void control_path(char *out, size_t capacity, const char *suffix) {
    snprintf(out, capacity, "%s.%s.%s", settings.control, settings.name, suffix);
}

static void apply_control(struct timespec *seen, off_t *seen_size) {
    struct stat info;
    char text[IMPAIR_TEXT_MAX];
    char path[PATH_LENGTH];
    ImpairProfile profile;

    if (stat(settings.control, &info) < 0) {
        return;
    }

    if (info.st_mtim.tv_sec == seen->tv_sec && info.st_mtim.tv_nsec == seen->tv_nsec && info.st_size == *seen_size) {
        return;
    }

    *seen = info.st_mtim;
    *seen_size = info.st_size;

    FILE *file = fopen(settings.control, "r");
    size_t length = file ? fread(text, 1, sizeof(text) - 1, file) : 0;

    if (file) {
        fclose(file);
    }

    text[length] = '\0';

    if (impair_parse(text, &profile) < 0) {
        control_path(path, sizeof(path), "error");
        write_file(path, text);
        say("ignored a profile it cannot read: %s", text);

        return;
    }

    uint64_t seed = 0;

    if (getrandom(&seed, sizeof(seed), 0) != (ssize_t)sizeof(seed)) {
        seed = (uint64_t)now_us();
    }

    current = profile;
    seed_base = seed | 1;
    active_flows = 0;

    impair_init(&impair, &profile, seed);
    memset(flows, 0, sizeof(flows));
    impair_describe(&profile, profile_text, sizeof(profile_text));
    control_path(path, sizeof(path), "applied");
    write_file(path, profile_text);
    say("applied %s", profile_text);
}

static void write_stats(void) {
    char path[PATH_LENGTH];
    char text[768];

    snprintf(text, sizeof(text),
        "{\"name\":\"%s\",\"profile\":\"%s\",\"packets\":%llu,\"bytes\":%llu,\"lost\":%llu,\"overflowed\":%llu,"
        "\"flows\":%llu,\"held\":%zu,\"held_dropped\":%llu,\"sent\":%llu,\"send_errors\":%llu}\n",
        settings.name, profile_text, (unsigned long long)total_packets, (unsigned long long)total_bytes,
        (unsigned long long)total_lost, (unsigned long long)total_overflowed, (unsigned long long)active_flows,
        held_bytes, (unsigned long long)held_dropped, (unsigned long long)sent, (unsigned long long)send_errors);
    control_path(path, sizeof(path), "stats");
    write_file(path, text);
}

static int earlier(const Held *left, const Held *right) {
    return left->release_us < right->release_us || (left->release_us == right->release_us && left->order < right->order);
}

static void heap_push(Held *packet) {
    size_t index = heap_count++;

    while (index > 0) {
        size_t parent = (index - 1) / 2;

        if (!earlier(packet, heap[parent])) {
            break;
        }

        heap[index] = heap[parent];
        index = parent;
    }

    heap[index] = packet;
}

static Held *heap_pop(void) {
    Held *top = heap[0];
    Held *last = heap[--heap_count];
    size_t index = 0;

    for (;;) {
        size_t child = index * 2 + 1;

        if (child >= heap_count) {
            break;
        }

        if (child + 1 < heap_count && earlier(heap[child + 1], heap[child])) {
            child++;
        }

        if (!earlier(heap[child], last)) {
            break;
        }

        heap[index] = heap[child];
        index = child;
    }

    if (heap_count > 0) {
        heap[index] = last;
    }

    return top;
}

static void hold(const unsigned char *data, size_t length, int64_t release_us) {
    if (held_bytes + length > HELD_MAX_BYTES) {
        held_dropped++;

        return;
    }

    if (heap_count == heap_capacity) {
        size_t capacity = heap_capacity ? heap_capacity * 2 : 1024;
        Held **grown = realloc(heap, capacity * sizeof(Held *));

        if (!grown) {
            held_dropped++;

            return;
        }

        heap = grown;
        heap_capacity = capacity;
    }

    Held *packet = malloc(sizeof(Held) + length);

    if (!packet) {
        held_dropped++;

        return;
    }

    packet->release_us = release_us;
    packet->order = held_order++;
    packet->length = length;

    memcpy(packet->data, data, length);
    heap_push(packet);

    held_bytes += length;
}

static void release_due(int sender, int64_t now) {
    while (heap_count > 0 && heap[0]->release_us <= now) {
        Held *packet = heap_pop();
        struct sockaddr_in target;

        memset(&target, 0, sizeof(target));

        target.sin_family = AF_INET;

        memcpy(&target.sin_addr, packet->data + 16, 4);

        if (sendto(sender, packet->data, packet->length, 0, (struct sockaddr *)&target, sizeof(target)) < 0) {
            send_errors++;
        }
        else {
            sent++;
        }

        held_bytes -= packet->length;

        free(packet);
    }
}

static Impair *flow_of(const unsigned char *packet, size_t length, int64_t now) {
    Flow key;
    size_t header = (size_t)(packet[0] & 0x0F) * 4;
    int fragment = ((packet[6] & 0x1F) << 8 | packet[7]) != 0;

    memset(&key, 0, sizeof(key));

    key.protocol = packet[9];

    memcpy(&key.source, packet + 12, 4);
    memcpy(&key.target, packet + 16, 4);

    if ((key.protocol == 6 || key.protocol == 17) && !fragment && length >= header + 4) {
        key.source_port = (uint16_t)(packet[header] << 8 | packet[header + 1]);
        key.target_port = (uint16_t)(packet[header + 2] << 8 | packet[header + 3]);
    }

    uint64_t hash = 1469598103934665603ULL;
    uint32_t parts[4] = { key.source, key.target, (uint32_t)key.source_port << 16 | key.target_port, key.protocol };

    for (int part = 0; part < 4; part++) {
        hash = (hash ^ parts[part]) * 1099511628211ULL;
    }

    Flow *oldest = NULL;

    for (int probe = 0; probe < FLOW_PROBES; probe++) {
        Flow *flow = &flows[(hash + (uint64_t)probe) % FLOW_SLOTS];

        if (flow->used && flow->source == key.source && flow->target == key.target &&
            flow->source_port == key.source_port && flow->target_port == key.target_port && flow->protocol == key.protocol) {
            flow->seen_us = now;

            return &flow->impair;
        }

        if (!flow->used) {
            oldest = flow;

            break;
        }

        if (!oldest || flow->seen_us < oldest->seen_us) {
            oldest = flow;
        }
    }

    if (!oldest->used) {
        active_flows++;
    }

    *oldest = key;
    oldest->used = 1;
    oldest->seen_us = now;

    impair_init(&oldest->impair, &current, seed_base ^ hash);

    return &oldest->impair;
}

static void read_packets(int tun, int64_t now) {
    unsigned char packet[PACKET_MAX];

    for (int round = 0; round < READ_BATCH; round++) {
        ssize_t length = read(tun, packet, sizeof(packet));

        if (length <= 0) {
            break;
        }

        if (length < 20 || (packet[0] >> 4) != 4) {
            continue;
        }

        Impair *state = current.per_flow ? flow_of(packet, (size_t)length, now) : &impair;
        uint64_t overflowed = state->overflowed;
        int64_t release = impair_packet(state, (size_t)length, now);

        total_packets++;
        total_bytes += (uint64_t)length;

        if (release >= 0) {
            hold(packet, (size_t)length, release);
        }
        else if (state->overflowed != overflowed) {
            total_overflowed++;
        }
        else {
            total_lost++;
        }
    }
}

static const char *env_text(const char *name, const char *fallback) {
    const char *value = getenv(name);

    return value && *value ? value : fallback;
}

int main(void) {
    ImpairProfile none;

    setvbuf(stderr, NULL, _IOLBF, 0);

    snprintf(settings.tun, sizeof(settings.tun), "%s", env_text("NETEM_TUN", "imp0"));
    snprintf(settings.subnet, sizeof(settings.subnet), "%s", env_text("NETEM_SUBNET", "172.31.250.0/24"));
    snprintf(settings.control, sizeof(settings.control), "%s", env_text("NETEM_CONTROL", "/lab-control/netem.conf"));
    snprintf(settings.name, sizeof(settings.name), "%s", env_text("NETEM_NAME", "contestant"));

    signal(SIGTERM, on_signal);
    signal(SIGINT, on_signal);

    memset(&none, 0, sizeof(none));
    impair_init(&impair, &none, 1);
    impair_describe(&none, profile_text, sizeof(profile_text));

    if (find_address() < 0) {
        say("no address in %s on this network namespace", settings.subnet);

        return 1;
    }

    int tun = open_tun();

    if (tun < 0 || route_through_tun() < 0) {
        return 1;
    }

    int sender = open_sender();

    if (sender < 0) {
        return 1;
    }

    say("traffic to %s leaves %s (%s, mtu %d) through %s; profile from %s", settings.subnet, settings.device,
        settings.address, settings.mtu, settings.tun, settings.control);

    struct timespec seen = { 0, 0 };
    off_t seen_size = -1;
    int64_t next_control = 0;
    int64_t next_stats = 0;

    while (running) {
        int64_t now = now_us();

        if (now >= next_control) {
            apply_control(&seen, &seen_size);

            next_control = now + CONTROL_MS * 1000;
        }

        if (now >= next_stats) {
            write_stats();

            next_stats = now + STATS_MS * 1000;
        }

        release_due(sender, now);

        int64_t wake = next_control < next_stats ? next_control : next_stats;

        if (heap_count > 0 && heap[0]->release_us < wake) {
            wake = heap[0]->release_us;
        }

        int timeout = wake > now ? (int)((wake - now + 999) / 1000) : 0;
        struct pollfd ready = { .fd = tun, .events = POLLIN };

        if (poll(&ready, 1, timeout) > 0 && (ready.revents & POLLIN)) {
            read_packets(tun, now_us());
        }
    }

    write_stats();
    say("stopping");

    return 0;
}
