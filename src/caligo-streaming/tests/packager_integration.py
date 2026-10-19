#!/usr/bin/env python3
import hashlib
import hmac
import json
import os
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY = b"integration-media-signing-key-0123456789"
INTERNAL = hmac.new(KEY, b"caligo-internal-v1", hashlib.sha256).hexdigest()


def expect(condition, message):
    if not condition:
        raise AssertionError(message)


def have(tool):
    return shutil.which(tool) is not None


class FakeApi:
    def __init__(self):
        self.jobs = []
        self.done = {}
        self.failed = {}
        self.extended = {}
        self.released = {}
        self.bad_keys = 0
        self.lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        api = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *arguments):
                pass

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                body = self.rfile.read(length) if length else b""
                if self.headers.get("X-Caligo-Key") != INTERNAL:
                    with api.lock:
                        api.bad_keys += 1
                    return self.reply(401, b"")
                parts = self.path.strip("/").split("/")
                with api.lock:
                    if self.path == "/internal/v1/packaging/claim":
                        if not api.jobs:
                            return self.reply(204, b"")
                        job = api.jobs.pop(0)
                        return self.reply(200, json.dumps(job).encode())
                    if len(parts) == 5 and parts[:3] == ["internal", "v1", "packaging"]:
                        content_id = int(parts[3])
                        payload = json.loads(body or b"{}")
                        target = {"done": api.done, "failed": api.failed, "extend": api.extended, "release": api.released}.get(parts[4])
                        if target is None:
                            return self.reply(404, b"")
                        if parts[4] == "extend":
                            target[content_id] = target.get(content_id, 0) + 1
                        else:
                            target[content_id] = payload
                        api.changed.notify_all()
                        return self.reply(204, b"")
                return self.reply(404, b"")

            def reply(self, status, body):
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if body:
                    self.wfile.write(body)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def add(self, job):
        with self.lock:
            self.jobs.append(job)

    def wait_for(self, content_id, timeout):
        deadline = time.monotonic() + timeout
        with self.lock:
            while content_id not in self.done and content_id not in self.failed and content_id not in self.released:
                left = deadline - time.monotonic()
                if left <= 0:
                    return None
                self.changed.wait(left)
            if content_id in self.done:
                return "done", self.done[content_id]
            if content_id in self.failed:
                return "failed", self.failed[content_id]
            return "released", self.released[content_id]

    def stop(self):
        self.server.shutdown()


def boxes(path):
    with open(path, "rb") as handle:
        data = handle.read()
    found = []
    position = 0
    while position + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[position:position + 8])
        if size < 8:
            break
        found.append((kind.decode("latin-1"), data[position + 8:position + min(size, 32)]))
        position += size
    return found


def make_sources(incoming):
    clip = os.path.join(incoming, "clip.mp4")
    subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
        "-t", "10", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "96k", "-metadata", "location=+52.3676+004.9041/", clip,
    ], check=True)
    with open(os.path.join(incoming, "clip.English.srt"), "w") as handle:
        handle.write("1\n00:00:01,000 --> 00:00:03,000\nHello <b>there</b>\n\n2\n00:00:05,000 --> 00:00:07,500\nSecond cue\n")
    with open(os.path.join(incoming, "clip.Spanish.vtt"), "w") as handle:
        handle.write("this is not a subtitle file at all")
    with open(os.path.join(incoming, "notes.mp4"), "w") as handle:
        handle.write("plain text pretending to be a video\n")
    with open(os.path.join(incoming, "playlist.mp4"), "w") as handle:
        handle.write("#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nfile:///etc/passwd\n#EXT-X-ENDLIST\n")


class Packager:
    def __init__(self, binary, api, incoming, library, ffmpeg="ffmpeg"):
        self.heartbeat = os.path.join(library, "..", "alive")
        self.log = tempfile.NamedTemporaryFile(prefix="packager-", suffix=".log", delete=False)
        environment = dict(os.environ)
        environment.update({
            "MEDIA_SIGNING_KEY": KEY.decode(),
            "API_HOST": "127.0.0.1",
            "API_PORT": str(api.port),
            "PACKAGER_INCOMING": incoming,
            "PACKAGER_LIBRARY": library,
            "PACKAGER_POLL_SECONDS": "1",
            "PACKAGER_PRESET": "ultrafast",
            "PACKAGER_LADDER": "720:2800,360:800",
            "PACKAGER_HEARTBEAT": self.heartbeat,
            "PACKAGER_FFMPEG": ffmpeg,
        })
        self.process = subprocess.Popen([binary], env=environment, stdout=self.log, stderr=self.log)

    def stop(self):
        self.process.send_signal(signal.SIGTERM)
        try:
            return self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            return -9


def job(content_id, source, storage=None, subtitles=None):
    return {
        "content_id": content_id,
        "source_file": source,
        "attempts": 1,
        "storage_path": storage,
        "claim_seconds": 6,
        "subtitles": subtitles or [],
    }


def test_a_clip_becomes_cmaf_with_hls_dash_and_progressive(api, library):
    os.makedirs(os.path.join(library, "3", "20200101000000-deadbeef"))
    os.makedirs(os.path.join(library, "3", "20190101000000-00000000"))
    with open(os.path.join(library, "3", "20200101000000-deadbeef", "master.m3u8"), "w") as handle:
        handle.write("old package")
    api.add(job(3, "clip.mp4", storage="3/20200101000000-deadbeef", subtitles=[
        {"file": "clip.English.srt", "language_id": 2, "language": "English", "tag": "en"},
        {"file": "clip.Spanish.vtt", "language_id": 1, "language": "Spanish", "tag": "es"},
    ]))
    started = time.monotonic()
    outcome = api.wait_for(3, 240)
    expect(outcome is not None, "the packager never reported back")
    kind, report = outcome
    expect(kind == "done", "the job %s: %s" % (kind, report))
    took = time.monotonic() - started
    storage = report["storage_path"]
    package = os.path.join(library, storage)
    expect(storage.startswith("3/") and os.path.isdir(package), "storage %s" % storage)
    expect(report["hls_path"] == "master.m3u8" and report["dash_path"] == "manifest.mpd", "paths %s" % report)
    expect(9.5 < report["duration_seconds"] < 10.5, "duration %s" % report["duration_seconds"])
    kinds = sorted(item["kind"] for item in report["renditions"])
    expect(kinds == ["audio", "progressive", "video", "video"], "renditions %s" % kinds)
    sizes = sorted((item["width"], item["height"]) for item in report["renditions"] if item["kind"] == "video")
    expect(sizes == [(640, 360), (1280, 720)], "video sizes %s" % sizes)
    progressive = [item for item in report["renditions"] if item["kind"] == "progressive"][0]
    expect((progressive["width"], progressive["height"]) == (640, 360), "progressive size %s" % progressive)
    expect(progressive["codec"].startswith("avc1.") and progressive["codec"].endswith(",mp4a.40.2"), progressive["codec"])
    expect(report["subtitles"] == [{"language_id": 2, "uri": "subs/2.vtt"}], "subtitles %s" % report["subtitles"])

    with open(os.path.join(package, "master.m3u8")) as handle:
        master = handle.read()
    expect(master.count("#EXT-X-STREAM-INF") == 2, master)
    expect('TYPE=AUDIO,GROUP-ID="audio"' in master and 'TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en"' in master, master)
    expect("FRAME-RATE=25.000" in master and "PROGRAM-DATE-TIME" not in master, master)

    for item in report["renditions"]:
        expect(os.path.isfile(os.path.join(package, item["path"])), "missing %s" % item["path"])
        if item["path"].endswith(".m3u8"):
            with open(os.path.join(package, item["path"])) as handle:
                playlist = handle.read()
            expect("#EXT-X-PLAYLIST-TYPE:VOD" in playlist and playlist.rstrip().endswith("#EXT-X-ENDLIST"), item["path"])
            expect("PROGRAM-DATE-TIME" not in playlist, "ffmpeg's wall-clock tags should be gone")
            for line in playlist.splitlines():
                if line and not line.startswith("#"):
                    expect(os.path.getsize(os.path.join(package, line)) > 0, "segment %s" % line)

    leftovers = [name for name in os.listdir(package) if name.startswith("media_") or name.endswith(".part")]
    expect(not leftovers, "ffmpeg's own playlists were left behind: %s" % leftovers)

    init = [name for name in os.listdir(package) if name.startswith("init-")][0]
    ftyp = boxes(os.path.join(package, init))[0]
    expect(ftyp[0] == "ftyp" and b"cmfc" in ftyp[1], "the init segment is not CMAF: %r" % (ftyp,))
    expect([kind for kind, _ in boxes(os.path.join(package, "progressive.mp4"))][:2] == ["ftyp", "moov"], "the MP4 is not fast-start")

    with open(os.path.join(package, "manifest.mpd")) as handle:
        mpd = handle.read()
    expect('mimeType="text/vtt" lang="en"' in mpd and "<BaseURL>subs/2.vtt</BaseURL>" in mpd, "no subtitle set in the manifest")
    expect(mpd.count("<Representation ") == 4, "representations in the manifest: %d" % mpd.count("<Representation "))

    with open(os.path.join(package, "subs", "2.vtt")) as handle:
        vtt = handle.read()
    expect(vtt.startswith("WEBVTT") and "00:01.000 --> 00:03.000" in vtt, vtt[:200])
    expect(os.path.isfile(os.path.join(package, "subs", "2.m3u8")), "no subtitle playlist")
    expect(not os.path.exists(os.path.join(package, "subs", "1.vtt")), "the broken Spanish sidecar should be skipped")

    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format_tags", "-of", "json", os.path.join(package, "progressive.mp4")],
                           capture_output=True, text=True, check=True)
    expect("location" not in probe.stdout, "the source's location metadata leaked into the MP4")

    remaining = sorted(os.listdir(os.path.join(library, "3")))
    expect(remaining == [storage.split("/")[1]], "old and unfinished packages should be gone, found %s" % remaining)
    return "%.1f s: 2 video + audio + MP4, CMAF brand, fast-start, English subtitles, old folders removed" % took


def test_text_disguised_as_video_fails_cleanly(api, library):
    api.add(job(4, "notes.mp4"))
    outcome = api.wait_for(4, 60)
    expect(outcome is not None and outcome[0] == "failed", "outcome %s" % (outcome,))
    expect(not os.path.exists(os.path.join(library, "4")) or not os.listdir(os.path.join(library, "4")), "files were left behind")
    return "failed: %s" % outcome[1]["error"][:90]


def test_a_playlist_is_never_opened_as_a_source(api, library):
    api.add(job(5, "playlist.mp4"))
    outcome = api.wait_for(5, 60)
    expect(outcome is not None and outcome[0] == "failed", "outcome %s" % (outcome,))
    expect("root:" not in json.dumps(outcome[1]), "file contents leaked into the error")
    return "failed: %s" % outcome[1]["error"][:90]


def test_unsafe_names_and_missing_files_fail(api, library):
    api.add(job(6, "../clip.mp4"))
    api.add(job(7, "gone.mp4"))
    first = api.wait_for(6, 30)
    second = api.wait_for(7, 30)
    expect(first and first[0] == "failed" and "plain file name" in first[1]["error"], "unsafe name: %s" % (first,))
    expect(second and second[0] == "failed" and "no longer" in second[1]["error"], "missing file: %s" % (second,))
    return "both refused before ffmpeg ran"


def test_shutting_down_mid_job_releases_it(api, library, packager):
    api.add(job(8, "clip.mp4"))
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline and not os.path.isdir(os.path.join(library, "8")):
        time.sleep(0.1)
    time.sleep(1.0)
    code = packager.stop()
    outcome = api.wait_for(8, 30)
    expect(outcome is not None and outcome[0] in ("released", "done"), "outcome %s" % (outcome,))
    if outcome[0] == "released":
        expect(not os.listdir(os.path.join(library, "8")), "the half-made package should be removed")
    expect(code == 0, "exit code %s" % code)
    return "SIGTERM %s the job and exited 0" % ("released" if outcome[0] == "released" else "let it finish, then")


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-packager")
    if not (have("ffmpeg") and have("ffprobe")):
        print("SKIP  the packager checks need ffmpeg and ffprobe on PATH")
        return 0
    root = tempfile.mkdtemp(prefix="packager-")
    incoming = os.path.join(root, "incoming")
    library = os.path.join(root, "library")
    os.makedirs(incoming)
    os.makedirs(library)
    make_sources(incoming)
    slow_ffmpeg = os.path.join(root, "slow-ffmpeg")
    with open(slow_ffmpeg, "w") as handle:
        handle.write("#!/bin/sh\nsleep 2.5\nexec ffmpeg \"$@\"\n")
    os.chmod(slow_ffmpeg, 0o755)
    api = FakeApi()
    packager = Packager(binary, api, incoming, library, ffmpeg=slow_ffmpeg)
    tests = [
        test_a_clip_becomes_cmaf_with_hls_dash_and_progressive,
        test_text_disguised_as_video_fails_cleanly,
        test_a_playlist_is_never_opened_as_a_source,
        test_unsafe_names_and_missing_files_fail,
    ]
    failed = 0
    try:
        for test in tests:
            name = test.__name__[5:].replace("_", " ")
            try:
                print("PASS  %s  -- %s" % (name, test(api, library)))
            except Exception as error:
                failed += 1
                print("FAIL  %s  -- %s: %s" % (name, type(error).__name__, error))
            if packager.process.poll() is not None:
                print("FAIL  the packager exited with code %s" % packager.process.returncode)
                failed += 1
                break
        if packager.process.poll() is None:
            try:
                print("PASS  shutting down mid job releases it  -- %s" % test_shutting_down_mid_job_releases_it(api, library, packager))
            except Exception as error:
                failed += 1
                print("FAIL  shutting down mid job releases it  -- %s: %s" % (type(error).__name__, error))
        if not os.path.exists(packager.heartbeat):
            failed += 1
            print("FAIL  the heartbeat file was never written")
        if api.bad_keys:
            failed += 1
            print("FAIL  the packager sent a wrong internal key %d time(s)" % api.bad_keys)
        extended = sum(api.extended.values())
        print("PASS  claims were extended %d time(s) while ffmpeg ran" % extended if extended else "FAIL  the claim was never extended")
        failed += 0 if extended else 1
    finally:
        if packager.process.poll() is None:
            packager.stop()
        api.stop()
    total = len(tests) + 3
    print("%d of %d packager checks failed" % (failed, total) if failed else "all %d packager checks passed" % total)
    if failed:
        packager.log.flush()
        with open(packager.log.name, "rb") as log:
            print("--- packager log (tail) ---")
            print(log.read()[-4000:].decode("utf-8", "replace"))
    shutil.rmtree(root, ignore_errors=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
