#!/usr/bin/env python3
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

SERVER = "netem-server"
CLIENT = "netem-client"
SERVER_IP = "10.250.0.1"
CLIENT_IP = "10.250.0.2"
SUBNET = "10.250.0.0/24"

ECHO = """
import socket
server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
server.bind(("%s", 9999))
while True:
    data, peer = server.recvfrom(2048)
    server.sendto(data, peer)
""" % SERVER_IP

COUNT_ECHOES = """
import socket, time
client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
client.connect(("%s", 9999))
client.setblocking(False)
received = 0
for index in range(2000):
    client.send(index.to_bytes(4, "big") + bytes(96))
    time.sleep(0.001)
    while True:
        try:
            client.recv(2048)
            received += 1
        except BlockingIOError:
            break
deadline = time.time() + 1
while time.time() < deadline:
    try:
        client.recv(2048)
        received += 1
    except BlockingIOError:
        time.sleep(0.01)
print(received)
""" % SERVER_IP

BULK_SENDER = """
import socket, time
server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("%s", 9998))
server.listen(1)
connection, _ = server.accept()
block = bytes(65536)
deadline = time.time() + 5
try:
    while time.time() < deadline:
        connection.sendall(block)
except OSError:
    pass
connection.close()
""" % SERVER_IP

PAIR_SENDER = """
import socket, threading, time
server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("%s", 9997))
server.listen(2)
def pump(connection):
    block = bytes(65536)
    deadline = time.time() + 5
    try:
        while time.time() < deadline:
            connection.sendall(block)
    except OSError:
        pass
    connection.close()
threads = []
for _ in range(2):
    connection, _ = server.accept()
    thread = threading.Thread(target=pump, args=(connection,))
    thread.start()
    threads.append(thread)
for thread in threads:
    thread.join()
""" % SERVER_IP

BULK_RECEIVER = """
import socket, sys, time
for attempt in range(50):
    try:
        client = socket.create_connection(("%s", int(sys.argv[1])), timeout=5)
        break
    except OSError:
        time.sleep(0.1)
total = 0
started = None
while True:
    data = client.recv(65536)
    if not data:
        break
    now = time.time()
    if started is None:
        started = now
    if now - started > 1:
        total += len(data)
    last = now
print(total * 8 / (last - started - 1))
""" % SERVER_IP


def sh(*arguments, check=True):
    return subprocess.run(arguments, check=check, capture_output=True, text=True)


def inside(namespace, *arguments):
    return ["ip", "netns", "exec", namespace] + list(arguments)


def cleanup():
    for namespace in (SERVER, CLIENT):
        sh("ip", "netns", "del", namespace, check=False)


def setup():
    cleanup()
    sh("ip", "netns", "add", SERVER)
    sh("ip", "netns", "add", CLIENT)
    sh("ip", "link", "add", "nx-server", "type", "veth", "peer", "name", "nx-client")
    sh("ip", "link", "set", "nx-server", "netns", SERVER)
    sh("ip", "link", "set", "nx-client", "netns", CLIENT)
    for namespace, device, address in ((SERVER, "nx-server", SERVER_IP), (CLIENT, "nx-client", CLIENT_IP)):
        sh(*inside(namespace, "ip", "addr", "add", address + "/24", "dev", device))
        sh(*inside(namespace, "ip", "link", "set", device, "up"))
        sh(*inside(namespace, "ip", "link", "set", "lo", "up"))


def ping_ms(count=10):
    result = sh(*inside(CLIENT, "ping", "-c", str(count), "-i", "0.2", "-q", SERVER_IP), check=False)
    found = re.search(r"= [\d.]+/([\d.]+)/", result.stdout)
    return float(found.group(1)) if found else None


def python_in(namespace, code):
    return inside(namespace, sys.executable, "-I", "-c", code)


class Netem:
    def __init__(self, binary, folder):
        self.control = os.path.join(folder, "netem.conf")
        self.applied = self.control + ".server.applied"
        self.error = self.control + ".server.error"
        self.stats_path = self.control + ".server.stats"
        self.log = open(os.path.join(folder, "netem.log"), "wb")
        environment = dict(os.environ, NETEM_SUBNET=SUBNET, NETEM_CONTROL=self.control, NETEM_NAME="server")
        self.process = subprocess.Popen(inside(SERVER, binary), env=environment, stdout=self.log, stderr=self.log)

    def apply(self, text, expect=None, seconds=5):
        previous = self.read(self.applied)
        with open(self.control + ".tmp", "w") as handle:
            handle.write(text)
        os.replace(self.control + ".tmp", self.control)
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            current = self.read(self.applied)
            if current and current != previous or (expect and current == expect):
                return current
            time.sleep(0.05)
        return self.read(self.applied)

    def stats(self):
        text = self.read(self.stats_path)
        return json.loads(text) if text else {}

    @staticmethod
    def read(path):
        try:
            with open(path) as handle:
                return handle.read()
        except OSError:
            return None

    def stop(self):
        self.process.terminate()
        try:
            return self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            return None


def main():
    binary = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "build/caligo-netem")
    if os.geteuid() != 0 or not shutil.which("ip") or not shutil.which("ping"):
        print("SKIP  netem integration  -- needs root, iproute2 and ping (network namespaces)")
        return 1 if os.environ.get("CI") else 0
    folder = tempfile.mkdtemp(prefix="netem-test-")
    failures = []
    passed = []
    netem = None
    helpers = []

    def check(name, condition, detail):
        if condition:
            passed.append(name)
            print("PASS  %s  -- %s" % (name, detail))
        else:
            failures.append(name)
            print("FAIL  %s  -- %s" % (name, detail))

    try:
        setup()
        baseline_before = ping_ms()
        netem = Netem(binary, folder)
        time.sleep(0.5)
        check("the sidecar starts and routes the subnet through its tun device", netem.process.poll() is None,
              "running, log: %s" % Netem.read(netem.log.name)[-200:].strip())

        applied = netem.apply("off")
        baseline = ping_ms()
        check("no profile adds no delay", applied is not None and baseline is not None and baseline < baseline_before + 5,
              "rtt %.2f ms before, %.2f ms through the sidecar" % (baseline_before or -1, baseline or -1))

        applied = netem.apply("delay_ms=80")
        delayed = ping_ms()
        check("delay adds to the round trip", delayed is not None and 78 <= delayed - baseline <= 90,
              "rtt %.1f ms with delay_ms=80 (%s)" % (delayed or -1, (applied or "").strip()))

        applied = netem.apply("delay_ms=20 jitter_ms=15")
        jittered = sh(*inside(CLIENT, "ping", "-c", "30", "-i", "0.05", "-q", SERVER_IP), check=False).stdout
        spread = re.search(r"= ([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)", jittered)
        check("jitter spreads the delay", spread is not None and float(spread.group(3)) - float(spread.group(1)) > 8,
              "min/avg/max %s" % (spread.group(0) if spread else jittered[-120:]))

        netem.apply("loss=0.3")
        echo = subprocess.Popen(python_in(SERVER, ECHO), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        helpers.append(echo)
        time.sleep(0.3)
        echoed = int(sh(*python_in(CLIENT, COUNT_ECHOES)).stdout.strip() or 0)
        check("loss drops packets on the way to listeners", 1240 <= echoed <= 1560,
              "%d of 2000 echoes came back with loss=0.3" % echoed)

        netem.apply("rate_kbit=4000 queue_ms=100")
        sender = subprocess.Popen(python_in(SERVER, BULK_SENDER), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        helpers.append(sender)
        time.sleep(0.3)
        rate = float(sh(*python_in(CLIENT, BULK_RECEIVER), "9998").stdout.strip() or 0)
        check("rate caps the throughput", 3.2e6 <= rate <= 4.4e6, "TCP %.2f Mbit/s with rate_kbit=4000" % (rate / 1e6))

        netem.apply("rate_kbit=3000 queue_ms=100 scope=flow")
        pair = subprocess.Popen(python_in(SERVER, PAIR_SENDER), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        helpers.append(pair)
        time.sleep(0.3)
        receivers = [subprocess.Popen(python_in(CLIENT, BULK_RECEIVER) + ["9997"], stdout=subprocess.PIPE, text=True)
                     for _ in range(2)]
        rates = [float(receiver.communicate(timeout=30)[0].strip() or 0) for receiver in receivers]
        flows = netem.stats().get("flows", 0)
        check("scope=flow gives every listener its own link", all(2.4e6 <= value <= 3.3e6 for value in rates) and flows >= 2,
              "two TCP flows at %s Mbit/s with rate_kbit=3000 scope=flow, %d flows" % (
                  " and ".join("%.2f" % (value / 1e6) for value in rates), flows))

        before = Netem.read(netem.applied)
        netem.apply("delay_ms=lots")
        time.sleep(0.6)
        check("a profile it cannot read is refused", Netem.read(netem.applied) == before and
              Netem.read(netem.error) == "delay_ms=lots", "error file written, previous profile kept")

        netem.apply("off")
        restored = ping_ms()
        stats = netem.stats()
        check("switching off restores the path", restored is not None and restored < baseline + 5,
              "rtt %.2f ms; stats %r" % (restored or -1, {key: stats.get(key) for key in ("packets", "lost", "overflowed", "sent")}))
        check("the stats count what happened", stats.get("lost", 0) > 400 and stats.get("sent", 0) > 1000 and
              stats.get("send_errors", 1) == 0, "lost %s, overflowed %s, sent %s, send errors %s" % (
                  stats.get("lost"), stats.get("overflowed"), stats.get("sent"), stats.get("send_errors")))

        code = netem.stop()
        netem = None
        time.sleep(0.2)
        after = ping_ms(5)
        check("the sidecar stops cleanly and leaves the path working", code == 0 and after is not None,
              "exit %r, rtt %.2f ms afterwards" % (code, after or -1))
    finally:
        for helper in helpers:
            helper.kill()
        if netem:
            netem.stop()
        cleanup()
    if failures:
        log = os.path.join(folder, "netem.log")
        if os.path.isfile(log):
            with open(log) as handle:
                print("--- netem log ---")
                print(handle.read()[-2000:])
        return 1
    shutil.rmtree(folder, ignore_errors=True)
    print("all %d netem checks passed" % len(passed))
    return 0


if __name__ == "__main__":
    sys.exit(main())
