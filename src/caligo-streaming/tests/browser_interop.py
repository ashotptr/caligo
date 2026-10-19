#!/usr/bin/env python3
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import integration as harness
from lite_interop import report_failures

PAGE = b"""<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>moq-lite over WebTransport</title></head>
<body><p id="status">loading</p><script type="module" src="/moq-browser.js"></script></body>
</html>
"""


class Page(BaseHTTPRequestHandler):
    bundle = b""
    reports = []
    arrived = threading.Event()

    def log_message(self, format, *args):
        pass

    def answer(self, status, kind, body):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/":
            self.answer(200, "text/html; charset=utf-8", PAGE)
        elif path == "/moq-browser.js":
            self.answer(200, "text/javascript", Page.bundle)
        else:
            self.answer(404, "text/plain", b"not found\n")

    def do_POST(self):
        if self.path != "/report":
            self.answer(404, "text/plain", b"not found\n")
            return
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        Page.reports.append(json.loads(body))
        Page.arrived.set()
        self.answer(204, "text/plain", b"")


def launch_browser(executable, url, folder):
    arguments = [executable, "--headless=new", "--user-data-dir=" + folder, "--no-first-run",
                 "--no-default-browser-check", "--disable-extensions", "--disable-gpu", "--mute-audio"]
    if hasattr(os, "geteuid") and os.geteuid() == 0:
        arguments.append("--no-sandbox")
    log = open(os.path.join(folder, "browser.log"), "wb")
    return subprocess.Popen(arguments + [url], stdout=log, stderr=log)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("binary", nargs="?", default="build/caligo-relay-quic")
    parser.add_argument("--browser", default=os.environ.get("CHROME") or shutil.which("google-chrome") or shutil.which("chromium"))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--relay-port", type=int, default=0)
    parser.add_argument("--page-port", type=int, default=0)
    parser.add_argument("--seconds", type=int, default=10)
    parser.add_argument("--external", action="store_true")
    parser.add_argument("--wait", type=int, default=180)
    args = parser.parse_args()

    bundle = os.path.join(os.path.dirname(os.path.abspath(__file__)), "interop", "build", "moq-browser.js")
    if not os.path.isfile(bundle):
        print("SKIP  browser interop  -- run npm ci and npm run bundle in tests/interop first")
        return harness.skipped()
    if not args.external and not args.browser:
        print("SKIP  browser interop  -- no Chrome found; pass --browser or set CHROME")
        return harness.skipped()

    with open(bundle, "rb") as handle:
        Page.bundle = handle.read()

    binary = os.path.abspath(args.binary)
    relay_port = args.relay_port or harness.free_port()
    environment = dict(os.environ)
    environment.update({
        "RELAY_LISTEN_HOST": args.host,
        "RELAY_LISTEN_PORT": str(relay_port),
        "RELAY_SOURCE_HOST": "127.0.0.1",
        "RELAY_SOURCE_PORT": str(harness.free_port()),
        "RELAY_SOURCE_PASSWORD": harness.PASSWORD,
    })
    log = open(os.path.join(os.path.dirname(binary), "browser-relay.log"), "wb")
    relay = subprocess.Popen([binary], env=environment, stdout=log, stderr=log)
    server = ThreadingHTTPServer((args.host, args.page_port), Page)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    folder = tempfile.mkdtemp(prefix="browser-interop-")
    browser = None
    source = None
    connection = None
    failures = []
    report = {}
    try:
        deadline = time.monotonic() + 10
        while True:
            try:
                connection = harness.SourceConnection(relay_port, "/live/opus", content_type="audio/ogg")
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.1)
        source = harness.OggSource(connection, 9150, "Interop Song", counter=0)
        source.start()
        time.sleep(1.5)

        url = "http://127.0.0.1:%d/?relay=http://127.0.0.1:%d/live/moq&seconds=%d" % (
            server.server_address[1], relay_port, args.seconds)
        if args.external:
            print("OPEN  %s" % url, flush=True)
        else:
            browser = launch_browser(args.browser, url, folder)

        if Page.arrived.wait(args.wait):
            report = Page.reports[-1]
            failures.extend(report_failures(report, args.seconds))
        else:
            failures.append("no report arrived within %d s" % args.wait)

        reply = harness.Client(relay_port, "/live/stats").head()
        stats = json.loads(reply.take(int(reply.headers["content-length"])))
        reply.close()
        quic = stats["quic"]
        if not (quic["accepted"] >= 1 and quic["failures"] == 0 and stats["moq"]["lite_accepted"] == 0):
            failures.append("the session came over WebTransport, not WebSocket: quic %r, lite_accepted %r" % (
                quic, stats["moq"]["lite_accepted"]))
    finally:
        if browser:
            browser.terminate()
            try:
                browser.wait(timeout=10)
            except subprocess.TimeoutExpired:
                browser.kill()
        if source:
            source.stop()
        if connection:
            connection.close()
        server.shutdown()
        relay.terminate()
        try:
            relay.wait(timeout=10)
        except subprocess.TimeoutExpired:
            relay.kill()

    audio = report.get("audio", {})
    if failures:
        for failure in failures:
            print("FAIL  browser interop  -- %s" % failure)
        print("--- report ---")
        print(json.dumps(report)[:2000])
        browser_log = os.path.join(folder, "browser.log")
        if os.path.isfile(browser_log):
            with open(browser_log, "rb") as handle:
                print("--- browser log (tail) ---")
                print(handle.read()[-2000:].decode("utf-8", "replace"))
        log.flush()
        with open(log.name, "rb") as handle:
            print("--- relay log (tail) ---")
            print(handle.read()[-2000:].decode("utf-8", "replace"))
        return 1
    shutil.rmtree(folder, ignore_errors=True)
    print("PASS  browser interop  -- %s read %d groups, %d packets in order over WebTransport (%s), connected in %d ms"
          % (report.get("userAgent", "?").split(" ")[-2], audio["groups"], audio["frames"], report["version"],
             report.get("connectMs", -1)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
