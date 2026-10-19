#!/usr/bin/env python3
import base64
import hashlib
import json
import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

BURST = 32768
MAX_LAG = 262144
METAINT = 8192
RATE = 128000
PASSWORD = "integration-source-pass-0123"
LIVE_BASE = 1000000
OPUS_PACKETS = 5
OPUS_PACKET_BYTES = 400
OPUS_PACKET_SAMPLES = 960
GRACE = 3

TRACKS = [
    ("Radiohead", "OK Computer", "Lucky", False),
    ("Arctic Monkeys", "AM", 'Why\'d You Only Call Me "When" You\'re High?', True),
    ("Mitski", "Bury Me at Makeout Creek", "Townie", False),
    ("Carla Morrison", "Déjenme Llorar", "Eres Tú", False),
]


def frame(sequence, padded):
    length = 418 if padded else 417
    header = bytes([0xFF, 0xFB, 0x92 if padded else 0x90, 0x00])
    body = bytearray((sequence * 7 + index) % 251 for index in range(length - 8))
    return header + struct.pack(">I", sequence) + bytes(body)


def info_frame():
    data = bytearray(417)
    data[0:4] = bytes([0xFF, 0xFB, 0x90, 0x00])
    data[36:40] = b"Xing"
    return bytes(data)


def id3v2(size):
    body = (bytes([0xFF, 0xFB, 0x90, 0x00]) * (size // 4 + 1))[:size]
    syncsafe = bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F, (size >> 7) & 0x7F, size & 0x7F])
    return b"ID3\x03\x00\x00" + syncsafe + body


def parse_frames(data, allow_partial_tail=True):
    sequences = []
    position = 0

    while position + 8 <= len(data):
        if data[position] != 0xFF or data[position + 1] != 0xFB or data[position + 2] not in (0x90, 0x92):
            raise AssertionError("byte %d is not a frame start: %r" % (position, data[position:position + 12]))

        length = 418 if data[position + 2] == 0x92 else 417

        if position + length > len(data):
            if not allow_partial_tail:
                raise AssertionError("the data ends inside a frame")
            break

        sequences.append(struct.unpack(">I", data[position + 4:position + 8])[0])
        position += length

    return sequences


def consecutive(sequences):
    return all(later == earlier + 1 for earlier, later in zip(sequences, sequences[1:]))


class FakeRadio:
    def __init__(self):
        self.lock = threading.Lock()
        self.audio = []
        self.events = []
        self.sequence = 0
        self.track = -1
        self.current = None
        self.pending = bytearray()
        self.running = False
        self.server = None
        self.port = 0
        self.threads = []
        self.track_frames = 600

    def start(self, port=0):
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind(("127.0.0.1", port))
        self.server.listen(64)
        self.port = self.server.getsockname()[1]
        self.running = True
        self.threads = [threading.Thread(target=self.accept, daemon=True), threading.Thread(target=self.broadcast, daemon=True)]
        for thread in self.threads:
            thread.start()

    def stop(self):
        self.running = False
        try:
            self.server.close()
        except OSError:
            pass
        with self.lock:
            for client in self.audio + self.events:
                try:
                    client.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                client.close()
            self.audio = []
            self.events = []
        for thread in self.threads:
            thread.join(timeout=3)

    def event(self):
        artist, album, title, request = self.current
        return ('data: {"playing": "public_html/music/%d.mp3", "artist": "%s", "album": "%s", "title": "%s", '
                '"bitrate": 128, "is_request": %s}\n\n' % (self.track, artist, album, title, "true" if request else "false")).encode()

    def accept(self):
        while self.running:
            try:
                client, _ = self.server.accept()
            except OSError:
                return
            try:
                client.settimeout(2)
                request = client.recv(1023).decode("latin1")
                client.settimeout(5)
                if "/events" in request or "Accept: text/event-stream" in request:
                    client.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
                                   b"Connection: keep-alive\r\nAccess-Control-Allow-Origin: *\r\n\r\n")
                    with self.lock:
                        if self.current:
                            client.sendall(self.event())
                        self.events.append(client)
                else:
                    client.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nTransfer-Encoding: chunked\r\n"
                                   b"Connection: keep-alive\r\nCache-Control: no-cache, no-store\r\n"
                                   b"Access-Control-Allow-Origin: *\r\n\r\n")
                    with self.lock:
                        self.audio.append(client)
            except OSError:
                client.close()

    def next_track(self):
        self.track += 1
        self.current = TRACKS[self.track % len(TRACKS)]
        data = bytearray(id3v2(3000 + self.track * 7))
        data += info_frame()
        for index in range(self.track_frames):
            data += frame(self.sequence, index % 3 == 0)
            self.sequence += 1
        data += b"TAG" + bytes(125)
        self.pending += data
        message = self.event()
        for client in list(self.events):
            try:
                client.sendall(message)
            except OSError:
                self.events.remove(client)

    def broadcast(self):
        started = time.monotonic()
        sent = 0
        while self.running:
            with self.lock:
                if len(self.pending) < 4096:
                    self.next_track()
                block = bytes(self.pending[:4096])
                del self.pending[:4096]
                packet = b"%x\r\n" % len(block) + block + b"\r\n"
                for client in list(self.audio):
                    try:
                        client.sendall(packet)
                    except OSError:
                        self.audio.remove(client)
                        client.close()
            sent += len(block)
            ahead = sent / RATE - (time.monotonic() - started)
            if ahead > 0:
                time.sleep(ahead)


class Client:
    def __init__(self, port, target="/live/", headers=None, version="1.1", method="GET", receive_buffer=None, raw=None):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if receive_buffer:
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, receive_buffer)
        self.socket.settimeout(10)
        self.socket.connect(("127.0.0.1", port))
        self.buffer = b""
        self.chunked = False
        self.chunk_left = 0
        self.status = 0
        self.headers = {}
        if raw is not None:
            if raw:
                self.socket.sendall(raw)
            return
        lines = ["%s %s HTTP/%s" % (method, target, version), "Host: relay.test"]
        for name, value in (headers or {}).items():
            lines.append("%s: %s" % (name, value))
        self.socket.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())

    def fill(self):
        data = self.socket.recv(65536)
        if not data:
            raise EOFError("closed")
        self.buffer += data

    def head(self):
        while b"\r\n\r\n" not in self.buffer:
            self.fill()
        head, self.buffer = self.buffer.split(b"\r\n\r\n", 1)
        lines = head.decode("latin1").split("\r\n")
        self.status = int(lines[0].split(" ")[1])
        self.headers = {}
        for line in lines[1:]:
            name, _, value = line.partition(":")
            self.headers[name.strip().lower()] = value.strip()
        self.chunked = self.headers.get("transfer-encoding", "").lower() == "chunked"
        return self

    def take(self, count):
        while len(self.buffer) < count:
            self.fill()
        data, self.buffer = self.buffer[:count], self.buffer[count:]
        return data

    def line(self):
        while b"\r\n" not in self.buffer:
            self.fill()
        line, self.buffer = self.buffer.split(b"\r\n", 1)
        return line

    def body(self, count):
        if not self.chunked:
            return self.take(count)
        data = b""
        while len(data) < count:
            if self.chunk_left == 0:
                self.chunk_left = int(self.line().split(b";")[0], 16)
                if self.chunk_left == 0:
                    raise EOFError("last chunk")
            part = self.take(min(self.chunk_left, count - len(data)))
            self.chunk_left -= len(part)
            data += part
            if self.chunk_left == 0:
                if self.take(2) != b"\r\n":
                    raise AssertionError("a chunk does not end with CRLF")
        return data

    def rest(self, timeout=3):
        self.socket.settimeout(timeout)
        try:
            while True:
                self.fill()
        except (EOFError, socket.timeout, ConnectionError):
            pass
        return self.buffer

    def closed_within(self, seconds):
        self.socket.settimeout(seconds)
        try:
            while True:
                if not self.socket.recv(65536):
                    return True
        except socket.timeout:
            return False
        except ConnectionError:
            return True

    def close(self):
        try:
            self.socket.close()
        except OSError:
            pass


class Relay:
    def __init__(self, binary, radio_port):
        self.binary = binary
        self.radio_port = radio_port
        self.port = free_port()
        self.process = None
        self.log = open(os.path.join(os.path.dirname(os.path.abspath(binary)), "integration-relay.log"), "wb")

    def start(self, extra=None):
        environment = dict(os.environ)
        environment.update({
            "RELAY_LISTEN_HOST": "127.0.0.1",
            "RELAY_LISTEN_PORT": str(self.port),
            "RELAY_SOURCE_HOST": "127.0.0.1",
            "RELAY_SOURCE_PORT": str(self.radio_port),
            "RELAY_BURST_BYTES": str(BURST),
            "RELAY_MAX_LAG_BYTES": str(MAX_LAG),
            "RELAY_RING_BYTES": str(1024 * 1024),
            "RELAY_ICY_METAINT": str(METAINT),
            "RELAY_SOURCE_TIMEOUT": "3",
            "RELAY_REQUEST_TIMEOUT": "2",
            "RELAY_MAX_LISTENERS": "24",
            "RELAY_SEND_BUFFER": "16384",
            "RELAY_STATION": "Test Station",
            "RELAY_SOURCE_PASSWORD": PASSWORD,
            "RELAY_SOURCE_GRACE": str(GRACE),
            "RELAY_SOURCE_MAX_AHEAD": "4",
        })
        environment.update(extra or {})
        self.process = subprocess.Popen([self.binary], env=environment, stderr=self.log, stdout=self.log)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                socket.create_connection(("127.0.0.1", self.port), timeout=0.5).close()
                return
            except OSError:
                time.sleep(0.05)
        raise AssertionError("the relay did not start listening")

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.send_signal(signal.SIGTERM)
            try:
                return self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                return -9
        return self.process.returncode if self.process else None

    def json(self, path):
        client = Client(self.port, path).head()
        length = int(client.headers["content-length"])
        data = client.take(length)
        client.close()
        return json.loads(data.decode("utf-8"))

    def stats(self):
        return self.json("/live/stats")

    def wait_for(self, condition, seconds=15, what="condition"):
        deadline = time.monotonic() + seconds
        last = None
        while time.monotonic() < deadline:
            last = self.stats()
            if condition(last):
                return last
            time.sleep(0.1)
        raise AssertionError("timed out waiting for %s; last stats: %s" % (what, json.dumps(last)[:600]))


def free_port():
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    return port


def skipped():
    return 1 if os.environ.get("CI") else 0


def expect(condition, message):
    if not condition:
        raise AssertionError(message)


def test_burst_starts_on_a_frame(relay, radio):
    relay.wait_for(lambda s: s["head"] >= BURST * 2 and s["source"]["state"] == "streaming", what="a full burst in the buffer")
    started = time.monotonic()
    client = Client(relay.port, "/live/", {"Range": "bytes=0-"}).head()
    data = client.body(BURST)
    elapsed = time.monotonic() - started
    expect(client.status == 200, "a first request is answered 200, got %d" % client.status)
    expect(client.headers.get("content-type") == "audio/mpeg", "content type is audio/mpeg")
    expect("content-range" not in client.headers, "a first answer carries no Content-Range")
    expect(client.chunked, "an HTTP/1.1 listener gets chunked transfer")
    expect(elapsed < 0.5, "the burst of %d bytes arrives at once, took %.3f s" % (BURST, elapsed))
    data += client.body(60000)
    sequences = parse_frames(data)
    expect(len(sequences) > 150 and consecutive(sequences), "frames arrive whole, in order, with nothing between them")
    client.close()
    return "burst of %d bytes in %.0f ms, %d frames in order" % (BURST, elapsed * 1000, len(sequences))


def test_listeners_share_the_same_stream(relay, radio):
    first = Client(relay.port, "/live/").head()
    time.sleep(0.4)
    second = Client(relay.port, "/live/").head()
    one = parse_frames(first.body(120000))
    two = parse_frames(second.body(80000))
    expect(consecutive(one) and consecutive(two), "both listeners get ordered frames")
    expect(set(one) & set(two), "the two listeners overlap in what they hear")
    expect(two[0] > one[0], "a later listener starts later")
    first.close()
    second.close()
    return "listeners overlap on frames %d..%d" % (max(one[0], two[0]), min(one[-1], two[-1]))


def test_resume_continues_exactly(relay, radio):
    before = relay.stats()["resumed_exact"]
    target = "/live/?t=resume-%d" % time.monotonic_ns()
    first = Client(relay.port, target).head()
    count = 50001
    head = first.body(count)
    first.close()
    time.sleep(0.3)
    second = Client(relay.port, target, {"Range": "bytes=%d-" % count}).head()
    expect(second.status == 206, "a resume is answered 206, got %d" % second.status)
    expect(second.headers.get("content-range") == "bytes %d-9007199254740990/*" % count,
           "Content-Range starts at the asked byte: %r" % second.headers.get("content-range"))
    expect(second.headers.get("accept-ranges") == "bytes", "the resume answer says Accept-Ranges")
    tail = second.body(60000)
    second.close()
    sequences = parse_frames(head + tail)
    expect(consecutive(sequences), "the resumed bytes continue the same stream without a gap or a repeat")
    expect(relay.stats()["resumed_exact"] == before + 1, "the resume is counted as exact")
    return "resumed at byte %d inside a frame, %d frames in order across the join" % (count, len(sequences))


def test_resume_without_a_known_session_starts_clean(relay, radio):
    before = relay.stats()["resumed_fresh"]
    client = Client(relay.port, "/live/", {"Range": "bytes=12345-"}).head()
    expect(client.status == 206, "answered 206, got %d" % client.status)
    expect(client.headers.get("content-range") == "bytes 12345-9007199254740990/*", "Content-Range starts at 12345")
    expect(consecutive(parse_frames(client.body(30000))), "the body starts on a frame boundary")
    client.close()
    stranger = Client(relay.port, "/live/?t=never-seen", {"Range": "bytes=777777-"}).head()
    expect(stranger.status == 206 and consecutive(parse_frames(stranger.body(20000))), "an unknown token also starts clean")
    stranger.close()
    expect(relay.stats()["resumed_fresh"] == before + 2, "both are counted as fresh starts")
    return "206 from a frame boundary for a plain URL and for an unknown token"


def test_old_clients_and_head(relay, radio):
    old = Client(relay.port, "/live/", version="1.0").head()
    expect(old.status == 200 and not old.chunked, "an HTTP/1.0 listener gets an unchunked body")
    expect(old.headers.get("connection") == "close", "and is told the connection closes")
    expect(consecutive(parse_frames(old.body(40000))), "with whole frames")
    old.close()
    head = Client(relay.port, "/live/", method="HEAD").head()
    expect(head.status == 200 and head.headers.get("content-type") == "audio/mpeg", "HEAD answers the stream headers")
    expect(head.rest(2) == b"", "HEAD has no body and the connection ends")
    head.close()
    return "HTTP/1.0 body unchunked, HEAD without body"


def read_icy(client, blocks):
    audio = b""
    titles = []
    for _ in range(blocks):
        audio += client.body(METAINT)
        length = client.body(1)[0] * 16
        if length:
            titles.append(client.body(length).rstrip(b"\x00").decode("utf-8"))
    return audio, titles


def test_icy_titles(relay, radio):
    client = Client(relay.port, "/live/", {"Icy-MetaData": "1"}).head()
    expect(client.headers.get("icy-metaint") == str(METAINT), "icy-metaint is announced")
    expect(client.headers.get("icy-name") == "Test Station", "icy-name is the station")
    audio, titles = read_icy(client, 1)
    expect(len(titles) == 1 and titles[0].startswith("StreamTitle='") and titles[0].endswith("';"),
           "the first block names the track: %r" % titles)
    more, repeated = read_icy(client, 1)
    audio += more
    titles += repeated
    deadline = time.monotonic() + 30
    while len(titles) < 2 and time.monotonic() < deadline:
        more, new = read_icy(client, 2)
        audio += more
        titles += new
    expect(len(titles) >= 2 and titles[0] != titles[1], "a new track brings a new title: %r" % titles)
    expect(consecutive(parse_frames(audio)), "the audio between the title blocks is intact")
    client.close()
    known = ["StreamTitle='%s - %s';" % (artist, title) for artist, _, title, _ in TRACKS]
    expect(all(title in known for title in titles), "titles are artist - title: %r" % titles)
    return "titles in band: %s" % " | ".join(title[13:-2] for title in titles[:2])


def read_event(client):
    while b"\n\n" not in client.buffer:
        client.fill()
    block, client.buffer = client.buffer.split(b"\n\n", 1)
    return block.decode("utf-8")


def test_events_and_now(relay, radio):
    client = Client(relay.port, "/live/events", {"Accept": "text/event-stream"}).head()
    expect(client.status == 200 and client.headers.get("content-type") == "text/event-stream", "events are an event stream")
    seen = []
    deadline = time.monotonic() + 40
    while len(seen) < 3 and time.monotonic() < deadline:
        block = read_event(client)
        if block.startswith("data: "):
            seen.append(json.loads(block[6:]))
    client.close()
    expect(len(seen) == 3, "three titles arrive as events")
    titles = [TRACKS[index][2] for index in range(len(TRACKS))]
    expect(all(entry["title"] in titles for entry in seen), "every event is valid JSON with a known title: %r" % seen)
    expect(len({entry["title"] for entry in seen}) == 3, "each event is a different track")
    now = relay.json("/live/now")
    expect(set(now) == {"on_air", "audible", "listeners", "source", "opus"}, "now has on_air, audible, listeners, source and opus")
    expect(now["source"] == "radio" and now["opus"] is False, "the radio feeds /live/ and /live/opus is silent")
    expect(now["on_air"]["title"] in titles and now["audible"]["title"] in titles, "both name a known track")
    return "events: %s" % " -> ".join(entry["title"][:22] for entry in seen)


def test_stats(relay, radio):
    stats = relay.stats()
    source = stats["source"]
    expect(source["state"] == "streaming" and source["events_state"] == "streaming", "both sources are connected")
    expect(source["bitrate_kbps"] == 128 and source["sample_rate"] == 44100 and source["channels"] == 2, "the format is read")
    expect(source["frames"] > 1000 and source["tag_bytes"] > 3000 and source["info_frames"] >= 1, "tags and info frames are dropped")
    expect(source["dropped_bytes"] < 4096 + 418, "only the join can cost bytes: %d" % source["dropped_bytes"])
    expect(stats["head"] - stats["tail"] <= 1024 * 1024 and stats["buffered_ms"] > 0, "the buffer is bounded")
    expect(stats["bytes_out"] > 0 and stats["accepted"] > 5, "traffic is counted")
    return "%d frames, %d tag bytes dropped, %d info frames, %d bytes lost at the join" % (
        source["frames"], source["tag_bytes"], source["info_frames"], source["dropped_bytes"])


def test_slow_listener_is_dropped(relay, radio):
    before = relay.stats()["dropped_lag"]
    slow = Client(relay.port, "/live/", receive_buffer=4096).head()
    expect(slow.status == 200, "the slow listener is accepted")
    relay.wait_for(lambda s: s["dropped_lag"] > before, seconds=30, what="the slow listener to be dropped")
    data = slow.rest(5)
    slow.close()
    expect(len(data) < MAX_LAG * 4, "the relay stopped feeding it")
    healthy = Client(relay.port, "/live/").head()
    expect(consecutive(parse_frames(healthy.body(40000))), "other listeners are unaffected")
    healthy.close()
    return "dropped after falling %d bytes behind" % MAX_LAG


def test_bad_requests(relay, radio):
    post = Client(relay.port, "/live/", method="POST").head()
    expect(post.status == 405, "POST is refused with 405, got %d" % post.status)
    post.close()
    missing = Client(relay.port, "/elsewhere").head()
    expect(missing.status == 404, "an unknown path is 404")
    missing.close()
    garbage = Client(relay.port, raw=b"NOT HTTP AT ALL\r\n\r\n").head()
    expect(garbage.status == 400, "garbage is 400")
    garbage.close()
    large = Client(relay.port, raw=b"GET /live/ HTTP/1.1\r\nX-Filler: " + b"a" * 6000).head()
    expect(large.status == 431, "oversized headers are 431, got %d" % large.status)
    large.close()
    health = Client(relay.port, "/healthz").head()
    expect(health.status == 200, "healthz answers 200")
    health.close()
    before = relay.stats()["dropped_slow_request"]
    silent = Client(relay.port, raw=b"")
    expect(silent.closed_within(6), "a connection that sends nothing is closed")
    silent.close()
    expect(relay.stats()["dropped_slow_request"] == before + 1, "and counted")
    return "405, 404, 400, 431 and a silent connection closed"


def test_listener_limit(relay, radio):
    held = []
    try:
        for _ in range(23):
            held.append(Client(relay.port, "/live/").head())
        time.sleep(0.3)
        extra = [Client(relay.port, "/live/") for _ in range(3)]
        refused = 0
        for client in extra:
            try:
                if client.head().status == 503:
                    refused += 1
            except (EOFError, ConnectionError, OSError):
                refused += 1
            client.close()
        expect(refused >= 2, "listeners beyond the limit are refused, %d of 3 were" % refused)
    finally:
        for client in held:
            client.close()
    relay.wait_for(lambda s: s["listeners"] == 0, what="the held listeners to leave")
    return "the 24-slot limit holds and frees up again"


def test_radio_restart(relay, radio):
    listener = Client(relay.port, "/live/").head()
    listener.body(BURST)
    connects = relay.stats()["source"]["connects"]
    port = radio.port
    radio.stop()
    relay.wait_for(lambda s: s["source"]["state"] != "streaming", what="the relay to notice the radio is gone")
    time.sleep(1.0)
    expect(relay.stats()["listeners"] >= 1, "the listener is kept while the radio is away")
    radio.sequence += 100000
    radio.pending = bytearray()
    radio.start(port)
    relay.wait_for(lambda s: s["source"]["state"] == "streaming" and s["source"]["connects"] > connects,
                   seconds=20, what="the relay to reconnect to the radio")
    deadline = time.monotonic() + 30
    fresh = []
    listener.socket.settimeout(2)
    while time.monotonic() < deadline and not any(sequence >= 100000 for sequence in fresh):
        try:
            fresh = parse_frames_loose(listener.body(8192))
        except socket.timeout:
            continue
    listener.close()
    expect(any(sequence >= 100000 for sequence in fresh), "the same connection carries the radio again after it returns")
    return "listener kept through the outage, audio resumed on the same connection"


def parse_frames_loose(data):
    sequences = []
    position = 0
    while position + 8 <= len(data):
        if data[position] == 0xFF and data[position + 1] == 0xFB and data[position + 2] in (0x90, 0x92):
            length = 418 if data[position + 2] == 0x92 else 417
            sequences.append(struct.unpack(">I", data[position + 4:position + 8])[0])
            position += length
        else:
            position += 1
    return sequences


def basic(user, password):
    return "Basic " + base64.b64encode(("%s:%s" % (user, password)).encode()).decode()


def crc_table():
    table = []
    for index in range(256):
        value = index << 24
        for _ in range(8):
            value = ((value << 1) ^ 0x04C11DB7) if value & 0x80000000 else value << 1
            value &= 0xFFFFFFFF
        table.append(value)
    return table


CRC_TABLE = crc_table()


def ogg_crc(data):
    crc = 0
    for byte in data:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ CRC_TABLE[((crc >> 24) ^ byte) & 0xFF]
    return crc


def ogg_page(flags, granule, serial, sequence, packets):
    lacing = bytearray()
    body = bytearray()
    for packet in packets:
        length = len(packet)
        while length >= 255:
            lacing.append(255)
            length -= 255
        lacing.append(length)
        body += packet
    page = bytearray(struct.pack("<4sBBqIII", b"OggS", 0, flags, granule, serial, sequence, 0))
    page += bytes([len(lacing)]) + lacing + body
    struct.pack_into("<I", page, 22, ogg_crc(page))
    return bytes(page)


def opus_head(channels=2):
    return b"OpusHead" + bytes([1, channels]) + struct.pack("<HIhB", 312, 48000, 0, 0)


def opus_tags(title, artist):
    comments = [("TITLE=%s" % title).encode(), ("ARTIST=%s" % artist).encode()]
    vendor = b"integration"
    data = b"OpusTags" + struct.pack("<I", len(vendor)) + vendor + struct.pack("<I", len(comments))
    for comment in comments:
        data += struct.pack("<I", len(comment)) + comment
    return data


def tags_title(packet):
    position = 8
    vendor = struct.unpack_from("<I", packet, position)[0]
    position += 4 + vendor
    count = struct.unpack_from("<I", packet, position)[0]
    position += 4
    found = {}
    for _ in range(count):
        size = struct.unpack_from("<I", packet, position)[0]
        name, _, value = packet[position + 4:position + 4 + size].decode("utf-8").partition("=")
        found[name.upper()] = value
        position += 4 + size
    return found


def parse_pages(data):
    pages = []
    position = 0
    while position + 27 <= len(data):
        if data[position:position + 4] != b"OggS":
            raise AssertionError("byte %d does not start a page: %r" % (position, data[position:position + 8]))
        segments = data[position + 26]
        if position + 27 + segments > len(data):
            break
        lacing = data[position + 27:position + 27 + segments]
        total = 27 + segments + sum(lacing)
        if position + total > len(data):
            break
        page = data[position:position + total]
        granule, serial, sequence, crc = struct.unpack_from("<qIII", page, 6)
        check = bytearray(page)
        check[22:26] = b"\0\0\0\0"
        if ogg_crc(check) != crc:
            raise AssertionError("page %d has a bad checksum" % sequence)
        packets = []
        current = b""
        offset = 27 + segments
        for value in lacing:
            current += page[offset:offset + value]
            offset += value
            if value < 255:
                packets.append(current)
                current = b""
        pages.append({"flags": page[5], "granule": granule, "serial": serial, "sequence": sequence, "packets": packets})
        position += total
    return pages, data[position:]


class OggListener:
    def __init__(self, client):
        self.client = client
        self.buffer = b""
        self.pages = []

    def read(self, count, timeout=20):
        target = len(self.pages) + count
        deadline = time.monotonic() + timeout
        while len(self.pages) < target:
            if time.monotonic() > deadline:
                raise AssertionError("only %d of %d pages arrived" % (len(self.pages), target))
            self.buffer += self.client.body(1024)
            pages, self.buffer = parse_pages(self.buffer)
            self.pages += pages
        return self.pages


def audio_counters(pages):
    return [struct.unpack(">I", packet[1:5])[0] for page in pages for packet in page["packets"]]


class SourceConnection:
    def __init__(self, port, target, method="PUT", content_type="audio/mpeg", password=PASSWORD, user="source",
                 expect=False, chunked=False, name="Integration Live"):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.chunked = chunked
        lines = ["%s %s %s" % (method, target, "HTTP/1.0" if method == "SOURCE" else "HTTP/1.1"),
                 "Host: relay.test", "User-Agent: integration-source/1.0", "Content-Type: %s" % content_type,
                 "ice-name: %s" % name]
        if password is not None:
            lines.append("Authorization: %s" % basic(user, password))
        if expect:
            lines.append("Expect: 100-continue")
        if chunked:
            lines.append("Transfer-Encoding: chunked")
        self.socket.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
        answer = b""
        while b"\r\n\r\n" not in answer:
            data = self.socket.recv(4096)
            if not data:
                break
            answer += data
        self.head = answer.split(b"\r\n\r\n", 1)[0].decode("latin1")
        self.status_line = self.head.split("\r\n")[0]
        self.status = int(self.status_line.split(" ")[1]) if self.status_line.startswith("HTTP/") else 0

    def send(self, data):
        if self.chunked:
            data = b"%x\r\n" % len(data) + data + b"\r\n"
        self.socket.sendall(data)

    def finish(self):
        self.socket.sendall(b"0\r\n\r\n")

    def close(self):
        try:
            self.socket.close()
        except OSError:
            pass


class Mp3Source(threading.Thread):
    def __init__(self, connection, base=LIVE_BASE, fast=False):
        super().__init__(daemon=True)
        self.connection = connection
        self.sequence = base
        self.fast = fast
        self.running = True
        self.error = None

    def run(self):
        started = time.monotonic()
        index = 0
        try:
            while self.running:
                self.connection.send(frame(self.sequence, index % 3 == 0))
                self.sequence += 1
                index += 1
                if not self.fast:
                    ahead = index * 1152 / 44100 - (time.monotonic() - started)
                    if ahead > 0:
                        time.sleep(ahead)
        except OSError as error:
            self.error = error

    def stop(self):
        self.running = False
        self.join(timeout=5)


class OggSource(threading.Thread):
    def __init__(self, connection, serial, title, counter=0, artist="Opus Artist"):
        super().__init__(daemon=True)
        self.connection = connection
        self.serial = serial
        self.title = title
        self.artist = artist
        self.counter = counter
        self.sequence = 0
        self.granule = 312
        self.pages = 0
        self.chain_request = None
        self.running = True
        self.error = None

    def headers(self):
        self.connection.send(ogg_page(0x02, 0, self.serial, 0, [opus_head()]))
        self.connection.send(ogg_page(0, 0, self.serial, 1, [opus_tags(self.title, self.artist)]))
        self.sequence = 2
        self.granule = 312

    def run(self):
        try:
            self.headers()
            started = time.monotonic()
            count = 0
            while self.running:
                request = self.chain_request
                packets = []
                for _ in range(OPUS_PACKETS):
                    packets.append(bytes([0xF8]) + struct.pack(">I", self.counter) + bytes(OPUS_PACKET_BYTES - 5))
                    self.counter += 1
                self.granule += OPUS_PACKETS * OPUS_PACKET_SAMPLES
                self.connection.send(ogg_page(0x04 if request else 0, self.granule, self.serial, self.sequence, packets))
                self.sequence += 1
                self.pages += 1
                count += 1
                if request:
                    self.serial, self.title = request
                    self.chain_request = None
                    self.headers()
                ahead = count * 0.1 - (time.monotonic() - started)
                if ahead > 0:
                    time.sleep(ahead)
        except OSError as error:
            self.error = error

    def chain(self, serial, title):
        self.chain_request = (serial, title)

    def stop(self):
        self.running = False
        self.join(timeout=5)


def admin(relay, query, password=PASSWORD):
    client = Client(relay.port, "/admin/metadata?" + query, {"Authorization": basic("source", password)}).head()
    length = int(client.headers.get("content-length", "0"))
    body = client.take(length) if length else b""
    client.close()
    return client.status, body


def runs(sequences):
    groups = []
    for sequence in sequences:
        live = sequence >= LIVE_BASE
        if not groups or groups[-1][0] != live:
            groups.append((live, []))
        groups[-1][1].append(sequence)
    return groups


def test_sources_need_the_password(relay, radio):
    before = relay.stats()["sources"]["unauthorized"]
    for password, user in ((None, "source"), ("wrong-password-123", "source"), (PASSWORD, "admin")):
        attempt = SourceConnection(relay.port, "/live/", password=password, user=user)
        expect(attempt.status == 401, "a source with user %s is refused with 401, got %r" % (user, attempt.status_line))
        expect("WWW-Authenticate: Basic" in attempt.head, "the refusal asks for Basic credentials")
        attempt.close()
    status, _ = admin(relay, "mode=updinfo&mount=%2Flive%2F&song=x", password="wrong-password-123")
    expect(status == 401, "a metadata update with the wrong password is 401, got %d" % status)
    unknown = SourceConnection(relay.port, "/live/elsewhere")
    expect(unknown.status == 404, "an unknown mount is 404, got %r" % unknown.status_line)
    unknown.close()
    wrong_type = SourceConnection(relay.port, "/live/opus", content_type="audio/mpeg")
    expect(wrong_type.status == 415, "MP3 sent to the Opus mount is 415, got %r" % wrong_type.status_line)
    wrong_type.close()
    status, _ = admin(relay, "mode=updinfo&mount=%2Flive%2F&song=Nobody+-+Here")
    expect(status == 400, "metadata for a mount without a source is 400, got %d" % status)
    public = Client(relay.port, "/live/admin/metadata?mode=updinfo&mount=%2Flive%2F&song=x").head()
    expect(public.status == 404, "the metadata endpoint is not under /live/, got %d" % public.status)
    public.close()
    stats = relay.stats()
    expect(stats["sources"]["unauthorized"] == before + 4, "four refusals are counted: %r" % stats["sources"])
    expect(not stats["live"]["connected"] and not stats["opus"]["connected"], "no source got in")
    return "401 without or with wrong credentials, 404 unknown mount, 415 wrong type, 400 metadata without a source"


def test_a_live_source_takes_over_and_falls_back(relay, radio):
    listener = Client(relay.port, "/live/").head()
    data = listener.body(40000)
    source_connection = SourceConnection(relay.port, "/live/", expect=True)
    expect(source_connection.status == 100, "PUT with Expect gets 100 Continue, got %r" % source_connection.status_line)
    source = Mp3Source(source_connection)
    source.start()
    relay.wait_for(lambda s: s["live"]["on_air"], what="the live source to go on air")
    status, body = admin(relay, "mode=updinfo&mount=%2Flive%2F&charset=UTF-8&song=DJ+Test+-+Live+Set")
    expect(status == 200 and b"Metadata update successful" in body, "the metadata update is accepted: %d %r" % (status, body))
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline and not any(sequence >= LIVE_BASE for sequence in parse_frames(data)[-40:]):
        data += listener.body(16000)
    data += listener.body(48000)
    now = relay.json("/live/now")
    expect(now["source"] == "live" and now["on_air"]["title"] == "Live Set" and now["on_air"]["artist"] == "DJ Test",
           "the live title is on air: %r" % now)
    source.stop()
    source_connection.close()
    relay.wait_for(lambda s: not s["live"]["connected"], what="the relay to notice the source left")
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline and parse_frames(data)[-1] >= LIVE_BASE:
        data += listener.body(16000)
    data += listener.body(32000)
    groups = runs(parse_frames(data))
    listener.close()
    expect([live for live, _ in groups] == [False, True, False], "radio, then live, then radio: %r" %
           [(live, len(sequences)) for live, sequences in groups])
    expect(all(consecutive(sequences) for _, sequences in groups), "every stretch is whole, in order, without a gap")
    now = relay.json("/live/now")
    titles = [track[2] for track in TRACKS]
    expect(now["source"] == "radio" and now["on_air"]["title"] in titles, "the radio title is back: %r" % now)
    return "same connection: %d radio frames, %d live frames, %d radio frames, all aligned" % tuple(
        len(sequences) for _, sequences in groups)


def test_source_method_chunked_put_and_one_source_per_mount(relay, radio):
    first = SourceConnection(relay.port, "/live/", method="SOURCE")
    expect(first.status_line.startswith("HTTP/1.0 200"), "SOURCE is answered HTTP/1.0 200, got %r" % first.status_line)
    source = Mp3Source(first, base=LIVE_BASE * 2)
    source.start()
    relay.wait_for(lambda s: s["live"]["on_air"] and s["live"]["method"] == "SOURCE", what="the SOURCE client to go on air")
    second = SourceConnection(relay.port, "/live/")
    expect(second.status == 403, "a second source for the same mount is 403, got %r" % second.status_line)
    second.close()
    source.stop()
    first.close()
    relay.wait_for(lambda s: not s["live"]["connected"], what="the SOURCE client to leave")
    chunked = SourceConnection(relay.port, "/live/", chunked=True)
    expect(chunked.status == 200, "a chunked PUT is answered 200, got %r" % chunked.status_line)
    source = Mp3Source(chunked, base=LIVE_BASE * 3)
    source.start()
    relay.wait_for(lambda s: s["live"]["on_air"], what="the chunked PUT to go on air")
    source.stop()
    chunked.finish()
    stats = relay.wait_for(lambda s: not s["live"]["connected"], what="the end of the chunked upload")
    chunked.close()
    expect(stats["live"]["last_drop"] == "finished the upload", "the last chunk ends the source: %r" % stats["live"]["last_drop"])
    return "SOURCE and chunked PUT both go on air, a second source is refused"


def test_a_source_that_is_too_fast_is_dropped(relay, radio):
    listener = Client(relay.port, "/live/").head()
    listener.body(20000)
    connection = SourceConnection(relay.port, "/live/")
    source = Mp3Source(connection, base=LIVE_BASE * 4, fast=True)
    source.start()
    stats = relay.wait_for(lambda s: s["live"]["last_drop"] == "sends faster than real time",
                           what="the fast source to be dropped")
    source.stop()
    connection.close()
    listener.body(64000)
    listener.close()
    expect(not stats["live"]["connected"], "the fast source is gone")
    return "dropped once it ran 4 s ahead of the clock, the listener kept playing"


def test_opus_needs_a_source(relay, radio):
    client = Client(relay.port, "/live/opus").head()
    expect(client.status == 503 and client.headers.get("retry-after") == "5", "no source is 503 with Retry-After")
    client.close()
    now = relay.json("/live/now")
    expect(now["opus"] is False, "/live/now says there is no Opus stream")
    return "503 with Retry-After until a source feeds /live/opus"


def check_audio_pages(pages, serial):
    expect(all(page["serial"] == serial for page in pages), "every page keeps the stream's serial number")
    expect(all(page["flags"] & 0x06 == 0 for page in pages), "no page after the headers starts or ends a stream")
    granules = [page["granule"] for page in pages]
    expect(all(later - earlier == OPUS_PACKETS * OPUS_PACKET_SAMPLES for earlier, later in zip(granules, granules[1:])),
           "granule positions advance by the page's samples: %r" % granules[:8])


def test_the_opus_mount_splices_chains_and_restarts(relay, radio):
    connection = SourceConnection(relay.port, "/live/opus", content_type="audio/ogg", expect=True)
    expect(connection.status == 100, "the Ogg source gets 100 Continue, got %r" % connection.status_line)
    source = OggSource(connection, 1111, "First Song")
    source.start()
    relay.wait_for(lambda s: s["opus"]["on_air"], what="the Opus source to go on air")
    client = Client(relay.port, "/live/opus").head()
    expect(client.status == 200 and client.headers.get("content-type") == "audio/ogg", "the Opus mount answers audio/ogg")
    expect(client.chunked, "and is chunked for HTTP/1.1")
    listener = OggListener(client)
    pages = listener.read(12)
    head, tags = pages[0], pages[1]
    serial = head["serial"]
    expect(head["flags"] == 0x02 and head["packets"][0][:8] == b"OpusHead", "the first page is OpusHead with BOS")
    expect(head["packets"][0][9] == 2, "the header says two channels")
    expect(tags["serial"] == serial and tags["packets"][0][:8] == b"OpusTags", "the second page is OpusTags")
    expect(tags_title(tags["packets"][0]).get("TITLE") == "First Song", "the tags carry the current title")
    expect(serial != 1111, "the relay writes its own serial number")
    check_audio_pages(pages[2:], serial)
    expect(consecutive(audio_counters(pages[2:])), "packets arrive in order")
    now = relay.json("/live/opus/now")
    expect(now["on_air"]["title"] == "First Song", "the Opus mount knows its title: %r" % now)
    expect(relay.json("/live/now")["opus"] is True, "/live/now says the Opus stream is on")
    events = Client(relay.port, "/live/opus/events", {"Accept": "text/event-stream"}).head()
    expect(json.loads(read_event(events)[6:])["title"] == "First Song", "the Opus events name the first song")
    source.chain(2222, "Second Song")
    deadline = time.monotonic() + 20
    seen = []
    while time.monotonic() < deadline and "Second Song" not in seen:
        block = read_event(events)
        if block.startswith("data: "):
            seen.append(json.loads(block[6:])["title"])
    events.close()
    expect("Second Song" in seen, "the new chain's title arrives as an event: %r" % seen)
    pages = listener.read(20)
    audio = pages[2:]
    check_audio_pages(audio, serial)
    expect(consecutive(audio_counters(audio)), "packets stay in order across the chain")
    source.stop()
    connection.close()
    relay.wait_for(lambda s: not s["opus"]["connected"], what="the relay to notice the Opus source left")
    time.sleep(1)
    again = SourceConnection(relay.port, "/live/opus", method="SOURCE", content_type="application/ogg")
    expect(again.status == 200, "a new source is accepted within the grace time, got %r" % again.status_line)
    returning = OggSource(again, 3333, "Third Song", counter=500000)
    returning.start()
    relay.wait_for(lambda s: s["opus"]["on_air"], what="the new Opus source to go on air")
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline and not any(counter >= 500000 for counter in audio_counters(listener.pages[-3:])):
        listener.read(1)
    listener.read(10)
    audio = listener.pages[2:]
    counters = audio_counters(audio)
    split = next(index for index, counter in enumerate(counters) if counter >= 500000)
    expect(consecutive(counters[:split]) and consecutive(counters[split:]), "both sources' packets arrive in order")
    expect(all(page["serial"] == serial for page in audio), "the restart keeps the listener's stream")
    expect(all(page["flags"] & 0x06 == 0 for page in audio), "without a new BOS page")
    granules = [page["granule"] for page in audio]
    expect(all(later > earlier for earlier, later in zip(granules, granules[1:])), "granules keep rising")
    expect(relay.json("/live/opus/now")["on_air"]["title"] == "Third Song", "the returning source's title is on air")
    before = relay.stats()["dropped_no_source"]
    returning.stop()
    again.close()
    expect(client.closed_within(GRACE + 4), "without a source the Opus listener is closed after the grace time")
    client.close()
    stats = relay.stats()
    expect(stats["dropped_no_source"] == before + 1, "and counted")
    gone = Client(relay.port, "/live/opus").head()
    expect(gone.status == 503, "the mount is 503 again")
    gone.close()
    return "headers first, %d chains spliced into serial %08x, restart kept the listener, closed %d s after the source left" % (
        stats["opus"]["chains"], serial, GRACE)


MOQ = "caligo-objects-0"
GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def read_varint(data, position):
    first = data[position]
    size = 1 << (first >> 6)
    value = first & 0x3F
    for index in range(1, size):
        value = (value << 8) | data[position + index]
    return value, position + size


class WebSocketClient:
    def __init__(self, port, protocols=MOQ, headers=None, receive_buffer=None, key=None):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if receive_buffer:
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, receive_buffer)
        self.socket.settimeout(10)
        self.socket.connect(("127.0.0.1", port))
        self.key = key or base64.b64encode(os.urandom(16)).decode()
        fields = {"Host": "relay.test", "Upgrade": "websocket", "Connection": "Upgrade",
                  "Sec-WebSocket-Key": self.key, "Sec-WebSocket-Version": "13"}
        if protocols:
            fields["Sec-WebSocket-Protocol"] = protocols
        fields.update(headers or {})
        lines = ["GET /live/moq HTTP/1.1"] + ["%s: %s" % item for item in fields.items() if item[1] is not None]
        self.socket.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
        data = b""
        while b"\r\n\r\n" not in data:
            chunk = self.socket.recv(4096)
            if not chunk:
                raise EOFError("closed during the handshake")
            data += chunk
        head, self.buffer = data.split(b"\r\n\r\n", 1)
        lines = head.decode("latin1").split("\r\n")
        self.status = int(lines[0].split(" ")[1])
        self.headers = {}
        for line in lines[1:]:
            name, _, value = line.partition(":")
            self.headers[name.strip().lower()] = value.strip()

    def accept_matches(self):
        expected = base64.b64encode(hashlib.sha1(self.key.encode() + GUID).digest()).decode()
        return self.headers.get("sec-websocket-accept") == expected

    def take(self, count):
        while len(self.buffer) < count:
            chunk = self.socket.recv(65536)
            if not chunk:
                raise EOFError("closed")
            self.buffer += chunk
        data, self.buffer = self.buffer[:count], self.buffer[count:]
        return data

    def frame(self):
        first, second = self.take(2)
        if second & 0x80:
            raise AssertionError("the server masked a frame")
        length = second & 0x7F
        if length == 126:
            length = struct.unpack(">H", self.take(2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self.take(8))[0]
        return first & 0x0F, self.take(length)

    def send(self, opcode, payload=b""):
        mask = os.urandom(4)
        header = bytes([0x80 | opcode])
        if len(payload) < 126:
            header += bytes([0x80 | len(payload)])
        else:
            header += bytes([0x80 | 126]) + struct.pack(">H", len(payload))
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.socket.sendall(header + mask + masked)

    def object(self):
        while True:
            opcode, payload = self.frame()
            if opcode == 0x9:
                self.send(0xA, payload)
                continue
            if opcode != 0x2:
                return opcode, payload
            if payload[0] != 1:
                raise AssertionError("not an object message: %r" % payload[:8])
            track, position = read_varint(payload, 1)
            group, position = read_varint(payload, position)
            index, position = read_varint(payload, position)
            return "object", (track, group, index, payload[position:])

    def close(self):
        try:
            self.socket.close()
        except OSError:
            pass


def audio_object(payload):
    if payload[0] != 0:
        raise AssertionError("audio payload version %d" % payload[0])
    timestamp, position = read_varint(payload, 1)
    packet = payload[position:]
    return timestamp, struct.unpack(">I", packet[1:5])[0], len(packet)


def collect(client, audio_count, timeout=20):
    catalog, meta, audio = [], [], []
    deadline = time.monotonic() + timeout
    while len(audio) < audio_count:
        if time.monotonic() > deadline:
            raise AssertionError("only %d audio objects arrived" % len(audio))
        kind, value = client.object()
        if kind != "object":
            raise AssertionError("unexpected frame opcode %r" % kind)
        track, group, index, payload = value
        if track == 0:
            catalog.append(json.loads(payload))
        elif track == 2:
            meta.append(json.loads(payload))
        else:
            audio.append((group, index) + audio_object(payload))
    return catalog, meta, audio


def check_audio_run(audio):
    for (group, index, timestamp, counter, _), (next_group, next_index, next_timestamp, next_counter, _) in zip(audio, audio[1:]):
        same_group = next_group == group and next_index == index + 1
        new_group = next_group == group + 1 and next_index == 0
        expect(same_group or new_group, "objects follow each other: (%d,%d) then (%d,%d)" % (group, index, next_group, next_index))
        expect(next_timestamp - timestamp == OPUS_PACKET_SAMPLES, "timestamps advance by one packet")
        expect(next_counter == counter + 1, "packets arrive in order")


def test_moq_handshake_and_refusals(relay, radio):
    cases = [
        ({"protocols": None}, 400),
        ({"protocols": "moq-lite-07"}, 400),
        ({"headers": {"Upgrade": None}}, 426),
        ({"headers": {"Sec-WebSocket-Version": "8"}}, 426),
        ({"key": "short"}, 400),
    ]
    for arguments, status in cases:
        attempt = WebSocketClient(relay.port, **arguments)
        expect(attempt.status == status, "%r is answered %d, got %d" % (arguments, status, attempt.status))
        attempt.close()
    client = WebSocketClient(relay.port, protocols="moq-lite-07, " + MOQ)
    expect(client.status == 101, "the upgrade is accepted, got %d" % client.status)
    expect(client.accept_matches(), "Sec-WebSocket-Accept is right")
    expect(client.headers.get("sec-websocket-protocol") == MOQ, "the relay picks %s" % MOQ)
    kind, (track, group, index, payload) = client.object()
    catalog = json.loads(payload)
    expect(track == 0 and index == 0, "the first object is the catalog")
    expect(catalog["on_air"] is False and [item["name"] for item in catalog["tracks"]] == ["audio/hi", "meta"],
           "the catalog lists audio/hi and meta and says nothing is on air: %r" % catalog)
    client.send(0x9, b"are you there")
    opcode, payload = client.frame()
    while opcode == 0x2:
        opcode, payload = client.frame()
    expect(opcode == 0xA and payload == b"are you there", "a ping is answered with the same payload")
    cookie = b"\x02" + struct.pack(">d", 1234.5)
    client.send(0x2, cookie)
    opcode, payload = client.frame()
    while opcode == 0x2 and payload[:1] == b"\x01":
        opcode, payload = client.frame()
    expect(opcode == 0x2 and payload[:9] == cookie, "a probe comes back with its cookie: %r" % payload[:12])
    edge, end = read_varint(payload, 9)
    expect(end == len(payload) and edge >= 0, "followed by the live edge in samples")
    client.send(0x8, struct.pack(">H", 1000))
    opcode, payload = client.frame()
    while opcode == 0x2:
        opcode, payload = client.frame()
    expect(opcode == 0x8 and payload == struct.pack(">H", 1000), "a close is echoed")
    expect(client.socket.recv(10) == b"", "and the connection ends")
    client.close()
    relay.wait_for(lambda s: s["moq"]["connections"] == 0, what="the WebSocket to be released")
    return "400 without the subprotocol, 426 without upgrade or version 13, 101 with the right accept key, ping and close"


def test_moq_listeners_share_groups_and_titles(relay, radio):
    connection = SourceConnection(relay.port, "/live/opus", content_type="audio/ogg")
    source = OggSource(connection, 4444, "Moq Song", counter=700000)
    source.start()
    try:
        relay.wait_for(lambda s: s["opus"]["on_air"], what="the Opus source to go on air")
        time.sleep(1.5)
        first = WebSocketClient(relay.port)
        time.sleep(0.4)
        second = WebSocketClient(relay.port)
        catalog, meta, audio = collect(first, 120)
        other_catalog, other_meta, other_audio = collect(second, 80)
        expect(catalog[0]["on_air"] is True and catalog[0]["tracks"][0]["channels"] == 2, "the catalog says on air, stereo")
        expect(meta and meta[0]["title"] == "Moq Song", "a joiner learns the current title at once: %r" % meta[:1])
        expect(audio[0][1] == 0 and other_audio[0][1] == 0, "both start at the beginning of a group")
        check_audio_run(audio)
        check_audio_run(other_audio)
        per_group = max(index for _, index, _, _, _ in audio) + 1
        expect(per_group == 50, "a group holds one second, 50 packets of 20 ms: %d" % per_group)
        mine = {timestamp: (group, index, counter) for group, index, timestamp, counter, _ in audio}
        shared = [item for item in other_audio if item[2] in mine]
        expect(shared and all(mine[timestamp] == (group, index, counter) for group, index, timestamp, counter, _ in shared),
               "both listeners see the same group, index and packet for the same moment")
        source.chain(5555, "Moq Second")
        deadline = time.monotonic() + 15
        titles = []
        while time.monotonic() < deadline and "Moq Second" not in titles:
            catalog, meta, more = collect(first, 10)
            titles += [entry["title"] for entry in meta]
            audio += more
        expect("Moq Second" in titles, "a new title arrives as a meta object: %r" % titles)
        stats = relay.stats()["moq"]
        expect(stats["connections"] == 2 and stats["objects_out"] > 200, "the relay counts both: %r" % stats)
        expect(relay.json("/live/now")["listeners"] >= 2, "/live/now counts low-latency listeners too")
        first.close()
        second.close()
        return "two listeners, groups of %d objects, same objects for the same moment, title change as meta" % per_group
    finally:
        source.stop()
        connection.close()
        relay.wait_for(lambda s: not s["opus"]["connected"], what="the Opus source to leave")


def test_moq_slow_reader_skips_ahead(relay, radio):
    connection = SourceConnection(relay.port, "/live/opus", content_type="audio/ogg")
    source = OggSource(connection, 6666, "Skip Song", counter=800000)
    source.start()
    try:
        relay.wait_for(lambda s: s["opus"]["on_air"], what="the Opus source to go on air")
        before = relay.stats()["moq"]["skips"]
        slow = WebSocketClient(relay.port, receive_buffer=4096)
        catalog, meta, audio = collect(slow, 5)
        time.sleep(5)
        catalog, meta, later = collect(slow, 200)
        skips = relay.stats()["moq"]["skips"]
        groups = [group for group, _, _, _, _ in audio + later]
        jumps = [(earlier, after) for earlier, after in zip(groups, groups[1:]) if after > earlier + 1]
        expect(skips > before, "the relay skipped ahead for the slow reader")
        expect(jumps, "the reader sees a jump over at least one whole group: %r" % sorted(set(groups))[:10])
        landing = [(group, index) for group, index, _, _, _ in audio + later if group == jumps[0][1]][0]
        expect(landing[1] == 0, "the jump lands on the start of a group: %r" % (landing,))
        check_audio_run(later[[index for index, item in enumerate(later) if item[0] == jumps[-1][1]][0]:])
        slow.close()
        return "%d skip(s), jumped from group %d to %d and kept going" % (skips - before, jumps[0][0], jumps[0][1])
    finally:
        source.stop()
        connection.close()
        relay.wait_for(lambda s: not s["opus"]["connected"], what="the Opus source to leave")


def ffmpeg_decode(path):
    result = subprocess.run(["ffmpeg", "-hide_banner", "-v", "error", "-i", path, "-f", "null", "-"],
                            capture_output=True, text=True, timeout=60)
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
                           capture_output=True, text=True, timeout=60)
    try:
        duration = float(probe.stdout.strip())
    except ValueError:
        duration = 0.0
    return result.returncode, result.stderr.strip(), duration


def test_ffmpeg_feeds_both_mounts(relay, radio):
    if not shutil.which("ffmpeg"):
        return "SKIP ffmpeg is not installed"
    base = "icecast://source:%s@127.0.0.1:%d" % (urllib.parse.quote(PASSWORD), relay.port)
    common = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi"]
    opus_source = subprocess.Popen(common + [
        "-i", "sine=frequency=440:sample_rate=48000", "-t", "14", "-ac", "2", "-c:a", "libopus", "-b:a", "96k",
        "-content_type", "audio/ogg", "-ice_name", "ffmpeg opus", "-f", "ogg", base + "/live/opus"],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    mp3_source = subprocess.Popen(common + [
        "-i", "sine=frequency=660:sample_rate=44100", "-t", "14", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k",
        "-legacy_icecast", "1", "-content_type", "audio/mpeg", "-ice_name", "ffmpeg mp3", "-f", "mp3", base + "/live/"],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    folder = tempfile.mkdtemp(prefix="relay-ffmpeg-")
    try:
        stats = relay.wait_for(lambda s: s["opus"]["on_air"] and s["live"]["on_air"], seconds=20,
                               what="both ffmpeg sources to go on air")
        expect(stats["live"]["method"] == "SOURCE" and stats["opus"]["method"] == "PUT", "ffmpeg used SOURCE and PUT")
        time.sleep(3)
        opus_client = Client(relay.port, "/live/opus").head()
        mp3_client = Client(relay.port, "/live/").head()
        opus_data = opus_client.body(48000)
        mp3_data = mp3_client.body(56000)
        opus_client.close()
        mp3_client.close()
        opus_path = os.path.join(folder, "live.opus.ogg")
        mp3_path = os.path.join(folder, "live.mp3")
        with open(opus_path, "wb") as handle:
            handle.write(opus_data)
        with open(mp3_path, "wb") as handle:
            handle.write(mp3_data)
        code, errors, opus_seconds = ffmpeg_decode(opus_path)
        expect(code == 0 and errors == "", "the Opus stream decodes cleanly: %r" % errors[:300])
        expect(opus_seconds >= 2.0, "and holds %.1f s of audio" % opus_seconds)
        code, errors, mp3_seconds = ffmpeg_decode(mp3_path)
        expect(code == 0, "the MP3 stream decodes: %r" % errors[:300])
        expect(mp3_seconds >= 2.0, "and holds %.1f s of audio" % mp3_seconds)
        for process in (opus_source, mp3_source):
            process.wait(timeout=30)
        failures = [process.stderr.read().decode("utf-8", "replace").strip() for process in (opus_source, mp3_source)
                    if process.returncode != 0]
        expect(not failures, "ffmpeg finished without errors: %r" % failures)
        relay.wait_for(lambda s: not s["opus"]["connected"] and not s["live"]["connected"],
                       what="both ffmpeg sources to leave")
        return "Opus %.1f s and MP3 %.1f s decoded by ffmpeg, MP3 decoder notes: %d" % (
            opus_seconds, mp3_seconds, len(errors.splitlines()) if errors else 0)
    finally:
        for process in (opus_source, mp3_source):
            if process.poll() is None:
                process.kill()
        shutil.rmtree(folder, ignore_errors=True)


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-relay")
    radio = FakeRadio()
    radio.start()
    relay = Relay(binary, radio.port)
    relay.start()
    tests = [
        test_burst_starts_on_a_frame,
        test_listeners_share_the_same_stream,
        test_resume_continues_exactly,
        test_resume_without_a_known_session_starts_clean,
        test_old_clients_and_head,
        test_icy_titles,
        test_events_and_now,
        test_stats,
        test_bad_requests,
        test_slow_listener_is_dropped,
        test_listener_limit,
        test_sources_need_the_password,
        test_a_live_source_takes_over_and_falls_back,
        test_source_method_chunked_put_and_one_source_per_mount,
        test_a_source_that_is_too_fast_is_dropped,
        test_opus_needs_a_source,
        test_the_opus_mount_splices_chains_and_restarts,
        test_moq_handshake_and_refusals,
        test_moq_listeners_share_groups_and_titles,
        test_moq_slow_reader_skips_ahead,
        test_ffmpeg_feeds_both_mounts,
        test_radio_restart,
    ]
    failed = 0
    try:
        for test in tests:
            name = test.__name__[5:].replace("_", " ")
            try:
                detail = test(relay, radio)
                if detail.startswith("SKIP "):
                    print("SKIP  %s  -- %s" % (name, detail[5:]))
                else:
                    print("PASS  %s  -- %s" % (name, detail))
            except Exception as error:
                failed += 1
                print("FAIL  %s  -- %s: %s" % (name, type(error).__name__, error))
            if relay.process.poll() is not None:
                print("FAIL  the relay exited with code %s" % relay.process.returncode)
                failed += 1
                break
    finally:
        code = relay.stop()
        radio.stop()
    if code != 0:
        print("FAIL  the relay exited with %s on SIGTERM, expected 0" % code)
        failed += 1
    else:
        print("PASS  the relay stops cleanly on SIGTERM")
    print("%d of %d integration checks failed" % (failed, len(tests) + 1) if failed else "all %d integration checks passed" % (len(tests) + 1))
    if failed:
        relay.log.flush()
        with open(relay.log.name, "rb") as log:
            print("--- relay log (tail) ---")
            print(log.read()[-3000:].decode("utf-8", "replace"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
