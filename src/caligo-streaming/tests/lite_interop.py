#!/usr/bin/env python3
import json
import os
import shutil
import subprocess
import sys
import time

import integration as harness

SECONDS = 12


def report_failures(report, seconds):
    failures = []
    audio = report.get("audio", {})

    def check(condition, message):
        if not condition:
            failures.append(message)

    check("error" not in report, "the client finished cleanly: %r" % report.get("error"))
    check(report.get("version") == "moq-lite-06", "negotiated moq-lite-06: %r" % report.get("version"))
    check({"kind": "announced", "path": "caligo/radio/main"} in report.get("announced", []),
          "the broadcast is announced: %r" % report.get("announced"))
    check(report.get("audioInfo", {}).get("timescale") == 48000, "the audio track uses a 48 kHz timescale")
    check(audio.get("groups", 0) >= seconds - 2, "a group per second arrived: %r" % audio.get("groups"))
    check(audio.get("sizes") == [50], "every group holds 50 packets of 20 ms: %r" % audio.get("sizes"))
    check(audio.get("countersInOrder") and audio.get("timestampsStep960") and audio.get("groupsInOrder"),
          "packets, timestamps and groups are in order")
    check(audio.get("tocs") == [0xF8], "the payload is the Opus packet itself")
    check(any("Interop Song" in text for text in report.get("meta", [])), "the meta track names the song")
    check(any(json.loads(text).get("on_air") for text in report.get("catalog", [])), "the catalog says on air")
    return failures


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-relay")
    folder = os.path.join(os.path.dirname(os.path.abspath(__file__)), "interop")
    if not shutil.which("node") or not os.path.isdir(os.path.join(folder, "node_modules", "@moq", "net")):
        print("SKIP  moq-lite interop  -- run npm ci in tests/interop with Node 22 first")
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
    log = open(os.path.join(os.path.dirname(binary), "interop-relay.log"), "wb")
    relay = subprocess.Popen([binary], env=environment, stdout=log, stderr=log)
    source = None
    connection = None
    failures = []
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
        source = harness.OggSource(connection, 5150, "Interop Song", counter=0)
        source.start()
        time.sleep(1.5)
        result = subprocess.run(
            ["node", os.path.join(folder, "moq_lite_client.mjs"), "http://127.0.0.1:%d/live/moq" % port, str(SECONDS)],
            cwd=folder, capture_output=True, text=True, timeout=SECONDS + 60)
        lines = [line for line in result.stdout.splitlines() if line.startswith("{")]
        report = json.loads(lines[-1]) if lines else {"error": result.stderr[-500:]}
        audio = report.get("audio", {})
        failures.extend(report_failures(report, SECONDS))
        if result.returncode != 0 and not failures:
            failures.append("the client exited with %d" % result.returncode)
        reply = harness.Client(port, "/live/stats").head()
        lite = json.loads(reply.take(int(reply.headers["content-length"])))["moq"]
        reply.close()
        if not (lite["lite_accepted"] >= 1 and lite["lite_failures"] == 0):
            failures.append("the relay counted no failures: %r" % lite)
        if failures:
            for failure in failures:
                print("FAIL  moq-lite interop  -- %s" % failure)
            print("--- client stderr (tail) ---")
            print(result.stderr[-2000:])
        else:
            print("PASS  moq-lite interop  -- moq-dev's @moq/net read %d groups, %d packets in order over qmux-02.moq-lite-06"
                  % (audio["groups"], audio["frames"]))
    finally:
        if source:
            source.stop()
        if connection:
            connection.close()
        relay.terminate()
        relay.wait(timeout=10)
    if failures:
        log.flush()
        with open(log.name, "rb") as handle:
            print("--- relay log (tail) ---")
            print(handle.read()[-2000:].decode("utf-8", "replace"))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
