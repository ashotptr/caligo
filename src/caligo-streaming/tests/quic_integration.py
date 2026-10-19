#!/usr/bin/env python3
import asyncio
import hashlib
import json
import os
import ssl
import subprocess
import sys
import time

import integration as harness

try:
    from aioquic.asyncio import connect
    from aioquic.asyncio.protocol import QuicConnectionProtocol
    from aioquic.h3.connection import H3_ALPN, FrameType, H3Connection
    from aioquic.h3.events import HeadersReceived, WebTransportStreamDataReceived
    from aioquic.quic.configuration import QuicConfiguration
    from aioquic.quic.events import StreamDataReceived, StreamReset
except ImportError:
    connect = None
    QuicConnectionProtocol = object

SECONDS = 6
PROTOCOL = "moq-lite-06"
BROADCAST = "caligo/radio/main"
TITLE = "Quic Song"


def varint(value):
    if value < 0x40:
        return bytes([value])
    if value < 0x4000:
        return (value | 0x4000).to_bytes(2, "big")
    if value < 0x40000000:
        return (value | 0x80000000).to_bytes(4, "big")
    return (value | 0xC000000000000000).to_bytes(8, "big")


def read_varint(data, position):
    if position >= len(data):
        raise IndexError("varint past the end")
    first = data[position]
    length = 1 << (first >> 6)
    if position + length > len(data):
        raise IndexError("varint past the end")
    value = first & 0x3F
    for index in range(1, length):
        value = (value << 8) | data[position + index]
    return value, position + length


def string(text):
    raw = text.encode()
    return varint(len(raw)) + raw


def message(body):
    return varint(len(body)) + body


def read_message(data, position):
    size, position = read_varint(data, position)
    if position + size > len(data):
        raise IndexError("message past the end")
    return bytes(data[position:position + size]), position + size


def read_string(data, position):
    size, position = read_varint(data, position)
    return bytes(data[position:position + size]).decode(), position + size


def subscribe(identifier, track):
    body = varint(identifier) + string(BROADCAST) + string(track) + bytes([0]) + varint(0) * 5
    return varint(2) + message(body)


def parse_group(data):
    kind, position = read_varint(data, 0)
    header, position = read_message(data, position)
    subscription, cursor = read_varint(header, 0)
    sequence, cursor = read_varint(header, cursor)
    start, cursor = read_varint(header, cursor)
    frames = []
    timestamp = 0
    while position < len(data):
        try:
            delta, after = read_varint(data, position)
            size, after = read_varint(data, after)
        except IndexError:
            break
        if after + size > len(data):
            break
        timestamp += (delta >> 1) if delta % 2 == 0 else -((delta + 1) >> 1)
        frames.append((timestamp, bytes(data[after:after + size])))
        position = after + size
    return {"kind": kind, "subscription": subscription, "sequence": sequence, "start": start, "frames": frames}


class MoqClient(QuicConnectionProtocol):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.h3 = H3Connection(self._quic, enable_webtransport=True)
        self.session = None
        self.response = None
        self.streams = {}
        self.ended = set()
        self.resets = {}

    def quic_event_received(self, event):
        if isinstance(event, StreamReset):
            self.resets[event.stream_id] = event.error_code
        for h3_event in self.h3.handle_event(event):
            if isinstance(h3_event, HeadersReceived) and h3_event.stream_id == self.session:
                if self.response and not self.response.done():
                    self.response.set_result({name.decode(): value.decode() for name, value in h3_event.headers})
            elif isinstance(h3_event, WebTransportStreamDataReceived):
                self.streams.setdefault(h3_event.stream_id, bytearray()).extend(h3_event.data)
                if h3_event.stream_ended:
                    self.ended.add(h3_event.stream_id)

    async def open(self, authority, path, protocols):
        self.session = self._quic.get_next_available_stream_id()
        self.response = asyncio.get_running_loop().create_future()
        headers = [(b":method", b"CONNECT"), (b":protocol", b"webtransport"), (b":scheme", b"https"),
                   (b":authority", authority.encode()), (b":path", path.encode()),
                   (b"origin", ("http://" + authority).encode())]
        if protocols:
            headers.append((b"wt-available-protocols", ", ".join('"%s"' % name for name in protocols).encode()))
        self.h3.send_headers(stream_id=self.session, headers=headers, end_stream=False)
        self.transmit()
        return await asyncio.wait_for(self.response, 5)

    def bidi(self, data, end=False):
        stream = self.h3.create_webtransport_stream(self.session, is_unidirectional=False)
        state = self.h3._get_or_create_stream(stream)
        state.frame_type = FrameType.WEBTRANSPORT_STREAM
        state.session_id = self.session
        self._quic.send_stream_data(stream, data, end_stream=end)
        self.transmit()
        return stream

    def uni(self, data):
        stream = self.h3.create_webtransport_stream(self.session, is_unidirectional=True)
        self._quic.send_stream_data(stream, data, end_stream=True)
        self.transmit()
        return stream

    def finish_session(self):
        self._quic.send_stream_data(self.session, b"", end_stream=True)
        self.transmit()

    def server_streams(self):
        return sorted(stream for stream in self.streams if stream % 4 == 3)

    def peer_certificate(self):
        certificate = getattr(self._quic.tls, "_peer_certificate", None)
        if certificate is None:
            return None
        from cryptography.hazmat.primitives.serialization import Encoding
        return certificate.public_bytes(Encoding.DER)


class BareClient(QuicConnectionProtocol):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.streams = {}
        self.ended = set()
        self.resets = {}

    def quic_event_received(self, event):
        if isinstance(event, StreamReset):
            self.resets[event.stream_id] = event.error_code
        elif isinstance(event, StreamDataReceived):
            self.streams.setdefault(event.stream_id, bytearray()).extend(event.data)
            if event.end_stream:
                self.ended.add(event.stream_id)

    def bidi(self, data, end=False):
        stream = self._quic.get_next_available_stream_id()
        self._quic.send_stream_data(stream, data, end_stream=end)
        self.transmit()
        return stream

    def uni(self, data):
        stream = self._quic.get_next_available_stream_id(is_unidirectional=True)
        self._quic.send_stream_data(stream, data, end_stream=True)
        self.transmit()
        return stream

    def finish_session(self):
        self._quic.close()
        self.transmit()

    server_streams = MoqClient.server_streams
    peer_certificate = MoqClient.peer_certificate


def configuration(alpn=None):
    settings = QuicConfiguration(is_client=True, alpn_protocols=alpn or H3_ALPN, max_datagram_frame_size=65536,
                                 verify_mode=ssl.CERT_NONE, server_name="localhost")
    return settings


def stats(port):
    reply = harness.Client(port, "/live/stats").head()
    body = reply.take(int(reply.headers["content-length"]))
    reply.close()
    return json.loads(body)["quic"]


def fingerprint(port):
    reply = harness.Client(port, "/certificate.sha256").head()
    body = reply.take(int(reply.headers["content-length"]))
    reply.close()
    return reply.status, body.decode()


async def listen(port, failures, seconds=SECONDS, report=None, bare=False):
    def check(condition, text):
        if not condition:
            failures.append(text)

    settings = configuration([PROTOCOL] if bare else None)
    async with connect("127.0.0.1", port, configuration=settings, create_protocol=BareClient if bare else MoqClient) as client:
        if bare:
            check(client._quic.tls.alpn_negotiated == PROTOCOL, "ALPN %s is negotiated" % PROTOCOL)
        else:
            headers = await client.open("127.0.0.1:%d" % port, "/live/moq", [PROTOCOL, "moq-lite-05"])
            check(headers.get(":status") == "200", "the CONNECT is accepted: %r" % headers)
            check(headers.get("wt-protocol", "").strip('"') == PROTOCOL, "the relay picks moq-lite-06: %r" % headers)

        status, hex_digest = fingerprint(port)
        der = client.peer_certificate()
        check(status == 200 and len(hex_digest) == 64, "/certificate.sha256 gives a SHA-256: %r" % hex_digest)
        if der is not None:
            check(hashlib.sha256(der).hexdigest() == hex_digest, "the fingerprint matches the certificate QUIC served")

        client.uni(varint(1) + message(varint(0)))
        announce = client.bidi(varint(1) + message(string("")))
        track = client.bidi(varint(6) + message(string(BROADCAST) + string("audio/hi")), end=True)
        audio = client.bidi(subscribe(1, "audio/hi"))
        meta = client.bidi(subscribe(2, "meta"))
        catalog = client.bidi(subscribe(3, "catalog"))

        await asyncio.sleep(seconds)

        live = stats(port)
        check(live["sessions"] >= 1, "the relay counts the session: %r" % live)

        try:
            reply, position = read_message(client.streams.get(announce, b""), 0)
            hop, cursor = read_varint(reply, 0)
            count, cursor = read_varint(reply, cursor)
            kind, position = read_varint(client.streams[announce], position)
            entry, _ = read_message(client.streams[announce], position)
            suffix, _ = read_string(entry, 0)
            check(count == 1 and kind == 0 and suffix == BROADCAST, "the broadcast is announced: %r" % suffix)
        except (IndexError, KeyError) as error:
            failures.append("the announce reply is complete: %r" % error)

        info = client.streams.get(track, b"")
        try:
            body, _ = read_message(info, 0)
            max_age, cursor = read_varint(body, 1)
            timescale, cursor = read_varint(body, cursor)
            check(track in client.ended and timescale == 48000 and max_age == 2000,
                  "track info: 48 kHz, 2 s max age, then FIN")
        except IndexError:
            failures.append("the track info arrived")

        try:
            kind, position = read_varint(client.streams.get(audio, b""), 0)
            started, _ = read_message(client.streams[audio], position)
            check(kind == 0 and read_varint(started, 0)[0] > 0, "the audio subscription starts at a group")
        except (IndexError, KeyError):
            failures.append("the audio subscription answered")

        groups = {1: [], 2: [], 3: []}
        for stream in client.server_streams():
            data = client.streams[stream]
            if data[:1] == b"\x01":
                continue
            group = parse_group(data)
            group["ended"] = stream in client.ended
            group["reset"] = client.resets.get(stream)
            groups.setdefault(group["subscription"], []).append(group)

        audio_groups = sorted(groups[1], key=lambda group: group["sequence"])
        complete = [group for group in audio_groups if group["ended"]]
        counters = [int.from_bytes(payload[1:5], "big") for group in audio_groups for _, payload in group["frames"]]
        sequences = [group["sequence"] for group in audio_groups]

        check(len(audio_groups) >= seconds - 2, "a group per second arrived: %d" % len(audio_groups))
        check(all(len(group["frames"]) == 50 for group in complete[1:]), "complete groups hold 50 packets of 20 ms")
        check(all(payload[0] == 0xF8 for group in audio_groups for _, payload in group["frames"]),
              "the payload is the Opus packet itself")
        check(counters == list(range(counters[0], counters[0] + len(counters))) if counters else False,
              "packets arrive in order with none missing")
        check(sequences == sorted(set(sequences)), "group sequences increase")
        check(all(frames[index + 1][0] - frames[index][0] == 960
                  for frames in (group["frames"] for group in audio_groups)
                  for index in range(len(frames) - 1)), "timestamps step by 960 samples")
        check(any(TITLE in payload.decode(errors="replace") for group in groups[2] for _, payload in group["frames"]),
              "the meta track names the song")
        check(any(json.loads(payload).get("on_air") for group in groups[3] for _, payload in group["frames"]),
              "the catalog says on air")

        if report is not None:
            report.update({"groups": len(audio_groups), "frames": len(counters), "first": sequences[:1],
                           "counters": counters})

        client.finish_session()
        await asyncio.sleep(0.5)


async def refused(port, path, protocols):
    async with connect("127.0.0.1", port, configuration=configuration(), create_protocol=MoqClient) as client:
        headers = await client.open("127.0.0.1:%d" % port, path, protocols)
        return headers.get(":status")


async def bare_refused(port, alpn):
    try:
        async with connect("127.0.0.1", port, configuration=configuration([alpn]), create_protocol=BareClient,
                           wait_connected=True):
            return "connected"
    except ConnectionError as error:
        return "refused: %s" % error


async def two_listeners(port, failures):
    first = {}
    second = {}
    await asyncio.gather(listen(port, failures, 4, first), listen(port, failures, 4, second))
    shared = set(first.get("counters", [])) & set(second.get("counters", []))
    if len(shared) < 100:
        failures.append("two sessions hear the same packets: %d shared" % len(shared))


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-relay-quic")
    if connect is None:
        print("SKIP  QUIC integration  -- pip install aioquic==1.2.0 first")
        return harness.skipped()
    port = harness.free_port()
    environment = dict(os.environ)
    environment.update({
        "RELAY_LISTEN_HOST": "127.0.0.1",
        "RELAY_LISTEN_PORT": str(port),
        "RELAY_SOURCE_HOST": "127.0.0.1",
        "RELAY_SOURCE_PORT": str(harness.free_port()),
        "RELAY_SOURCE_PASSWORD": harness.PASSWORD,
    })
    log = open(os.path.join(os.path.dirname(binary), "quic-relay.log"), "wb")
    relay = subprocess.Popen([binary], env=environment, stdout=log, stderr=log)
    source = None
    connection = None
    failures = []
    passed = []
    try:
        deadline = time.monotonic() + 10
        while True:
            try:
                connection = harness.SourceConnection(port, "/live/opus", content_type="audio/ogg")
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.1)
        source = harness.OggSource(connection, 7340, TITLE, counter=0)
        source.start()
        time.sleep(1.5)

        def run(name, coroutine):
            before = len(failures)
            try:
                asyncio.run(asyncio.wait_for(coroutine, 60))
            except Exception as error:
                failures.append("%s raised %r" % (name, error))
            if len(failures) == before:
                passed.append(name)
                print("PASS  %s" % name)
            else:
                for failure in failures[before:]:
                    print("FAIL  %s  -- %s" % (name, failure))

        run("a WebTransport session negotiates moq-lite-06 and streams the radio", listen(port, failures))
        run("bare QUIC with ALPN moq-lite-06 streams the radio too", listen(port, failures, bare=True))
        run("two sessions share the same packets", two_listeners(port, failures))

        statuses = {}

        async def refusals():
            statuses["none"] = await refused(port, "/live/moq", [])
            statuses["old"] = await refused(port, "/live/moq", ["moq-lite-03"])
            statuses["path"] = await refused(port, "/live/elsewhere", [PROTOCOL])
            statuses["bare"] = await bare_refused(port, "moq-lite-03")

        run("CONNECTs without moq-lite-06 or on another path are refused", refusals())
        if statuses and (statuses.get("none") == "200" or statuses.get("old") == "200" or statuses.get("path") == "200"
                         or statuses.get("bare") == "connected"):
            failures.append("refusals: %r" % statuses)
            print("FAIL  refusals  -- %r" % statuses)

        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and stats(port)["sessions"] > 0:
            time.sleep(0.2)
        final = stats(port)
        if (final["sessions"] == 0 and final["closed"] >= 4 and final["bare_accepted"] >= 1 and final["failures"] == 0
                and final["refused"] >= 2):
            passed.append("sessions close cleanly")
            print("PASS  sessions close cleanly  -- %r" % {key: final[key] for key in (
                "accepted", "bare_accepted", "closed", "refused", "groups_out", "frames_out")})
        else:
            failures.append("final stats %r" % final)
            print("FAIL  sessions close cleanly  -- %r" % final)
    finally:
        if source:
            source.stop()
        if connection:
            connection.close()
        relay.terminate()
        try:
            relay.wait(timeout=10)
        except subprocess.TimeoutExpired:
            relay.kill()
    if failures:
        log.flush()
        with open(log.name, "rb") as handle:
            print("--- relay log (tail) ---")
            print(handle.read()[-3000:].decode("utf-8", "replace"))
        return 1
    print("all %d QUIC checks passed" % len(passed))
    return 0


if __name__ == "__main__":
    sys.exit(main())
