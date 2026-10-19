#!/usr/bin/env python3
import base64
import hashlib
import hmac
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY = b"integration-media-signing-key-0123456789"
INTERNAL = hmac.new(KEY, b"caligo-internal-v1", hashlib.sha256).hexdigest()
STORAGE = "3/20261014120000-ab12cd34"
CACHE_MS = 400


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def token(session, expires, storage=STORAGE, key=KEY):
    scope = hashlib.sha256(storage.encode()).digest()[:8]
    payload = session.bytes + int(expires).to_bytes(8, "big") + scope
    mac = hmac.new(key, payload, hashlib.sha256).digest()[:16]
    return base64.urlsafe_b64encode(payload + mac).decode()


def expect(condition, message):
    if not condition:
        raise AssertionError(message)


class FakeApi:
    def __init__(self):
        self.sessions = {}
        self.lookups = {}
        self.bad_keys = 0
        self.down = False
        self.lock = threading.Lock()
        api = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *arguments):
                pass

            def do_GET(self):
                prefix = "/internal/v1/media/sessions/"
                if self.headers.get("X-Caligo-Key") != INTERNAL:
                    with api.lock:
                        api.bad_keys += 1
                    return self.reply(401, b"")
                if api.down:
                    return self.reply(500, b"down")
                if not self.path.startswith(prefix):
                    return self.reply(404, b"")
                session = self.path[len(prefix):]
                with api.lock:
                    api.lookups[session] = api.lookups.get(session, 0) + 1
                    state = api.sessions.get(session)
                if state is None:
                    return self.reply(404, b"")
                if state["state"] == "gone":
                    return self.reply(410, b"This playback session has ended.")
                body = json.dumps({
                    "session_id": session,
                    "live_until": state["live_until"],
                    "storage_path": state["storage"],
                }).encode()
                self.reply(200, body)

            def reply(self, status, body):
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def live(self, minutes=5, storage=STORAGE):
        session = uuid.uuid4()
        with self.lock:
            self.sessions[str(session)] = {"state": "live", "live_until": int(time.time()) + minutes * 60, "storage": storage}
        return session

    def set(self, session, **fields):
        with self.lock:
            self.sessions[str(session)].update(fields)

    def lookups_for(self, session):
        with self.lock:
            return self.lookups.get(str(session), 0)

    def stop(self):
        self.server.shutdown()


class Vod:
    def __init__(self, binary, api, library):
        self.port = free_port()
        self.log = tempfile.NamedTemporaryFile(prefix="vod-", suffix=".log", delete=False)
        environment = dict(os.environ)
        environment.update({
            "MEDIA_SIGNING_KEY": KEY.decode(),
            "API_HOST": "127.0.0.1",
            "API_PORT": str(api.port),
            "VOD_LIBRARY": library,
            "VOD_LISTEN_HOST": "127.0.0.1",
            "VOD_LISTEN_PORT": str(self.port),
            "VOD_CACHE_MS": str(CACHE_MS),
            "VOD_LOOKUP_TIMEOUT_MS": "1000",
            "VOD_MAX_CLIENTS": "200",
        })
        self.process = subprocess.Popen([binary], env=environment, stdout=self.log, stderr=self.log)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.5):
                    return
            except OSError:
                time.sleep(0.05)
        raise RuntimeError("the VOD server did not start")

    def stop(self):
        self.process.send_signal(signal.SIGTERM)
        try:
            return self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            return -9


def read_response(sock, head_only=False):
    data = b""
    while b"\r\n\r\n" not in data:
        chunk = sock.recv(65536)
        if not chunk:
            raise AssertionError("the connection closed before the headers ended: %r" % data[:200])
        data += chunk
    head, rest = data.split(b"\r\n\r\n", 1)
    lines = head.decode("latin-1").split("\r\n")
    status = int(lines[0].split(" ")[1])
    headers = {}
    for line in lines[1:]:
        name, value = line.split(":", 1)
        headers[name.strip().lower()] = value.strip()
    length = 0 if head_only else int(headers.get("content-length", "0"))
    while len(rest) < length:
        chunk = sock.recv(65536)
        if not chunk:
            raise AssertionError("the body ended early")
        rest += chunk
    return status, headers, rest[:length], rest[length:]


def request(vod, path, method="GET", headers=None, head_only=None):
    with socket.create_connection(("127.0.0.1", vod.port), timeout=5) as sock:
        extra = "".join("%s: %s\r\n" % item for item in (headers or {}).items())
        sock.sendall(("%s %s HTTP/1.1\r\nHost: test\r\n%s\r\n" % (method, path, extra)).encode())
        status, received, body, _ = read_response(sock, head_only if head_only is not None else method == "HEAD")
        return status, received, body


def build_library():
    root = tempfile.mkdtemp(prefix="vod-library-")
    package = os.path.join(root, STORAGE)
    os.makedirs(os.path.join(package, "subs"))
    files = {
        "master.m3u8": b"#EXTM3U\n#EXT-X-VERSION:7\n",
        "seg-0-00001.m4s": os.urandom(300000),
        "progressive.mp4": os.urandom(1500000),
        "subs/2.vtt": b"WEBVTT\n\n00:01.000 --> 00:02.000\nHello\n",
        "notes.txt": b"not media",
        "empty.m4s": b"",
    }
    for name, data in files.items():
        with open(os.path.join(package, name), "wb") as handle:
            handle.write(data)
    secret = os.path.join(root, "secret.txt")
    with open(secret, "wb") as handle:
        handle.write(b"outside the package")
    os.symlink(secret, os.path.join(package, "leak.m4s"))
    os.symlink(os.path.join(package, "subs"), os.path.join(package, "linked"))
    return root, files


def test_a_signed_segment_is_served(vod, api, files):
    session = api.live()
    status, headers, body = request(vod, "/vod/%s/seg-0-00001.m4s" % token(session, time.time() + 3600))
    expect(status == 200, "status %d" % status)
    expect(body == files["seg-0-00001.m4s"], "the body differs from the file")
    expect(headers["content-type"] == "video/iso.segment", headers["content-type"])
    expect(headers["cache-control"] == "private, no-store", headers["cache-control"])
    expect(headers["accept-ranges"] == "bytes", "no Accept-Ranges")
    status, headers, body = request(vod, "/vod/%s/subs/2.vtt" % token(session, time.time() + 3600))
    expect(status == 200 and body.startswith(b"WEBVTT"), "subtitle status %d" % status)
    expect(headers["content-type"].startswith("text/vtt"), headers["content-type"])
    status, _, _ = request(vod, "/%s/master.m3u8" % token(session, time.time() + 3600))
    expect(status == 200, "the /vod prefix should be optional, got %d" % status)
    return "200 with the file's bytes, media types and no-store"


def test_ranges_follow_rfc_9110(vod, api, files):
    session = api.live()
    path = "/vod/%s/progressive.mp4" % token(session, time.time() + 3600)
    data = files["progressive.mp4"]
    size = len(data)
    status, headers, body = request(vod, path, headers={"Range": "bytes=100-199"})
    expect(status == 206 and body == data[100:200], "plain range: %d" % status)
    expect(headers["content-range"] == "bytes 100-199/%d" % size, headers.get("content-range"))
    status, headers, body = request(vod, path, headers={"Range": "bytes=-50"})
    expect(status == 206 and body == data[-50:], "suffix range: %d" % status)
    status, headers, body = request(vod, path, headers={"Range": "bytes=%d-" % (size - 10)})
    expect(status == 206 and body == data[-10:], "open range: %d" % status)
    status, headers, _ = request(vod, path, headers={"Range": "bytes=%d-" % size})
    expect(status == 416 and headers["content-range"] == "bytes */%d" % size, "past the end: %d" % status)
    status, _, body = request(vod, path, headers={"Range": "bytes=0-1,5-9"})
    expect(status == 200 and body == data, "several ranges should fall back to the whole file, got %d" % status)
    status, _, body = request(vod, "/vod/%s/empty.m4s" % token(session, time.time() + 3600))
    expect(status == 200 and body == b"", "an empty file: %d" % status)
    return "206 slices, suffix and open ranges, 416 past the end, whole file for several ranges"


def test_head_and_keep_alive(vod, api, files):
    session = api.live()
    signed = token(session, time.time() + 3600)
    status, headers, body = request(vod, "/vod/%s/progressive.mp4" % signed, method="HEAD")
    expect(status == 200 and body == b"" and int(headers["content-length"]) == len(files["progressive.mp4"]), "HEAD %d" % status)
    with socket.create_connection(("127.0.0.1", vod.port), timeout=5) as sock:
        for name in ("master.m3u8", "seg-0-00001.m4s"):
            sock.sendall(("GET /vod/%s/%s HTTP/1.1\r\nHost: test\r\n\r\n" % (signed, name)).encode())
            status, headers, body, _ = read_response(sock)
            expect(status == 200 and body == files[name], "%s on a kept connection: %d" % (name, status))
        pipelined = "".join("GET /vod/%s/%s HTTP/1.1\r\nHost: test\r\n\r\n" % (signed, name) for name in ("subs/2.vtt", "master.m3u8"))
        sock.sendall(pipelined.encode())
        status, _, first, rest = read_response(sock)
        expect(status == 200 and first == files["subs/2.vtt"], "first pipelined answer")
        data = rest
        while b"\r\n\r\n" not in data:
            data += sock.recv(65536)
        expect(data.startswith(b"HTTP/1.1 200"), "second pipelined answer: %r" % data[:40])
    return "HEAD has the length and no body; one connection serves several and pipelined requests"


def test_forged_and_malformed_tokens(vod, api, files):
    session = api.live()
    signed = token(session, time.time() + 3600)
    forged = signed[:-1] + ("A" if signed[-1] != "A" else "B")
    other_key = token(session, time.time() + 3600, key=b"another-key-another-key-another-key!")
    for path in ("/vod/%s/master.m3u8" % forged, "/vod/%s/master.m3u8" % other_key, "/vod/%s/master.m3u8" % signed[:-2]):
        status, _, _ = request(vod, path)
        expect(status == 403, "%s gave %d" % (path[:30], status))
    status, _, _ = request(vod, "/vod/%s" % signed)
    expect(status == 404, "no file after the token: %d" % status)
    expect(api.lookups_for(session) == 0, "a forged token should not cost a session lookup")
    return "403 for changed, foreign and short tokens, without asking the API"


def test_only_package_files_are_served(vod, api, files):
    session = api.live()
    signed = token(session, time.time() + 3600)
    for name, why in (("../secret.txt", "a parent folder"), ("notes.txt", "a file that is not media"), (".hidden.m4s", "a hidden name"),
                      ("leak.m4s", "a symlink out of the library"), ("linked/2.vtt", "a symlinked folder"),
                      ("missing.m4s", "a missing file"), ("subs", "a folder")):
        status, _, body = request(vod, "/vod/%s/%s" % (signed, name))
        expect(status == 404, "%s (%s) gave %d %r" % (name, why, status, body[:40]))
    return "404 for parent folders, other files, hidden names, symlinks and folders"


def test_an_ended_session_dies_within_the_cache_time(vod, api, files):
    session = api.live()
    path = "/vod/%s/master.m3u8" % token(session, time.time() + 3600)
    expect(request(vod, path)[0] == 200, "the live session should play")
    api.set(session, state="gone")
    deadline = time.monotonic() + 5
    status = 200
    started = time.monotonic()
    while status == 200 and time.monotonic() < deadline:
        time.sleep(0.05)
        status = request(vod, path)[0]
    took = time.monotonic() - started
    expect(status == 410, "an ended session still answered %d" % status)
    expect(took < CACHE_MS / 1000 + 1.0, "it took %.2f s to notice" % took)
    return "410 %.2f s after the session ended (cache %d ms)" % (took, CACHE_MS)


def test_lapsed_replaced_unknown_and_expired(vod, api, files):
    lapsed = api.live()
    api.set(lapsed, live_until=int(time.time()) - 1)
    replaced = api.live(storage="3/20261015000000-ffffffff")
    unknown = uuid.uuid4()
    expired = api.live()
    results = {
        "lapsed lease": request(vod, "/vod/%s/master.m3u8" % token(lapsed, time.time() + 3600))[0],
        "replaced package": request(vod, "/vod/%s/master.m3u8" % token(replaced, time.time() + 3600))[0],
        "unknown session": request(vod, "/vod/%s/master.m3u8" % token(unknown, time.time() + 3600))[0],
        "expired address": request(vod, "/vod/%s/master.m3u8" % token(expired, time.time() - 1))[0],
    }
    expect(all(status == 410 for status in results.values()), str(results))
    expect(api.lookups_for(expired) == 0, "an expired address should not cost a lookup")
    return ", ".join("%s 410" % name for name in results)


def test_lookups_are_shared_and_cached(vod, api, files):
    session = api.live()
    path = "/vod/%s/seg-0-00001.m4s" % token(session, time.time() + 3600)
    with ThreadPoolExecutor(max_workers=20) as pool:
        statuses = list(pool.map(lambda _: request(vod, path)[0], range(40)))
    expect(all(status == 200 for status in statuses), "statuses %s" % set(statuses))
    lookups = api.lookups_for(session)
    expect(1 <= lookups <= 3, "%d lookups for 40 requests" % lookups)
    return "40 parallel requests, %d session lookup(s)" % lookups


def test_the_api_being_down_is_a_503(vod, api, files):
    session = api.live()
    path = "/vod/%s/master.m3u8" % token(session, time.time() + 3600)
    api.down = True
    try:
        status, headers, _ = request(vod, path)
        expect(status == 503 and headers.get("retry-after") == "2", "while down: %d" % status)
    finally:
        api.down = False
    time.sleep(0.6)
    status = request(vod, path)[0]
    expect(status == 200, "after the API came back: %d" % status)
    expect(api.bad_keys == 0, "the VOD server sent a wrong internal key %d time(s)" % api.bad_keys)
    return "503 with Retry-After while the API fails, 200 once it is back; the internal key was right"


def test_bad_requests(vod, api, files):
    session = api.live()
    path = "/vod/%s/master.m3u8" % token(session, time.time() + 3600)
    expect(request(vod, path, method="POST")[0] == 405, "POST")
    expect(request(vod, path, method="DELETE")[0] == 405, "DELETE")
    status, _, _ = request(vod, path, headers={"Content-Length": "5"})
    expect(status == 400, "a body: %d" % status)
    with socket.create_connection(("127.0.0.1", vod.port), timeout=5) as sock:
        sock.sendall(b"NONSENSE\r\n\r\n")
        expect(read_response(sock)[0] == 400, "garbage")
    with socket.create_connection(("127.0.0.1", vod.port), timeout=5) as sock:
        sock.sendall(b"GET / HTTP/1.1\r\nX-Big: " + b"a" * 9000 + b"\r\n\r\n")
        expect(read_response(sock)[0] == 431, "an oversized head")
    with socket.create_connection(("127.0.0.1", vod.port), timeout=5) as sock:
        sock.sendall(b"GET /vod/x\x00y/a.m4s HTTP/1.1\r\n\r\n")
        expect(read_response(sock)[0] == 400, "a NUL byte")
    return "405, 400 and 431 where they belong"


def test_health_and_stats(vod, api, files):
    expect(request(vod, "/healthz")[0] == 200, "healthz")
    expect(request(vod, "/vod/healthz")[0] == 200, "/vod/healthz")
    status, _, body = request(vod, "/vod/stats")
    stats = json.loads(body)
    expect(status == 200 and stats["service"] == "caligo-vod", "stats")
    expect(stats["gone"] > 0 and stats["forbidden"] > 0 and stats["partial"] > 0 and stats["cache_hits"] > 0, str(stats))
    return "%d requests, %d bytes out, %d lookups, %d cache hits" % (stats["requests"], stats["bytes_out"], stats["lookups"], stats["cache_hits"])


def download_like_a_player(vod, path, attempts=5):
    for attempt in range(attempts):
        status, headers, body = request(vod, path)
        if status != 503 or attempt == attempts - 1:
            return status, body, attempt
        time.sleep(float(headers.get("retry-after", "1")))


def test_many_downloads_at_once(vod, api, files):
    session = api.live()
    path = "/vod/%s/progressive.mp4" % token(session, time.time() + 3600)
    with ThreadPoolExecutor(max_workers=50) as pool:
        results = list(pool.map(lambda _: download_like_a_player(vod, path), range(100)))
    failed = [status for status, _, _ in results if status != 200]
    expect(not failed, "%d downloads failed even after retrying: %r" % (len(failed), sorted(set(failed))))
    expect(all(body == files["progressive.mp4"] for _, body, _ in results), "a 200 download came back with other bytes")
    retried = sum(1 for _, _, attempt in results if attempt > 0)
    return "100 downloads of 1.5 MB, 50 at a time, all intact (%d retried after a slow session lookup)" % retried


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-vod")
    library, files = build_library()
    api = FakeApi()
    vod = Vod(binary, api, library)
    tests = [
        test_a_signed_segment_is_served,
        test_ranges_follow_rfc_9110,
        test_head_and_keep_alive,
        test_forged_and_malformed_tokens,
        test_only_package_files_are_served,
        test_an_ended_session_dies_within_the_cache_time,
        test_lapsed_replaced_unknown_and_expired,
        test_lookups_are_shared_and_cached,
        test_the_api_being_down_is_a_503,
        test_bad_requests,
        test_many_downloads_at_once,
        test_health_and_stats,
    ]
    failed = 0
    try:
        for test in tests:
            name = test.__name__[5:].replace("_", " ")
            try:
                detail = test(vod, api, files)
                print("PASS  %s  -- %s" % (name, detail))
            except Exception as error:
                failed += 1
                print("FAIL  %s  -- %s: %s" % (name, type(error).__name__, error))
            if vod.process.poll() is not None:
                print("FAIL  the VOD server exited with code %s" % vod.process.returncode)
                failed += 1
                break
    finally:
        code = vod.stop()
        api.stop()
        shutil.rmtree(library, ignore_errors=True)
    if code != 0:
        print("FAIL  the VOD server exited with %s on SIGTERM, expected 0" % code)
        failed += 1
    else:
        print("PASS  the VOD server stops cleanly on SIGTERM")
    total = len(tests) + 1
    print("%d of %d VOD checks failed" % (failed, total) if failed else "all %d VOD checks passed" % total)
    if failed:
        vod.log.flush()
        with open(vod.log.name, "rb") as log:
            print("--- vod log (tail) ---")
            print(log.read()[-3000:].decode("utf-8", "replace"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
