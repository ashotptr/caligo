import array
import math
import os
import socket
import sys
import time

RATE = 48000
CHUNK = 960
BASE_HZ = 1000.0
STEP_HZ = 200.0
CODES = 16
BEEP_SECONDS = 0.06
TONE_HZ = 330.0
LEAD_SECONDS = 0.02
RESYNC_SECONDS = 0.5


def reference_target():
    target = os.environ.get("LAB_REFERENCE", "")
    if ":" not in target:
        return None
    host, port = target.rsplit(":", 1)
    return host, int(port)


def render(start, sample):
    stereo = array.array("h", bytes(CHUNK * 4))
    mono = array.array("h", bytes(CHUNK * 2))
    tone_step = 2 * math.pi * TONE_HZ / RATE
    for index in range(CHUNK):
        moment = start + (sample + index) / RATE
        fraction = moment % 1.0
        value = 0.08 * math.sin(tone_step * (sample + index))
        if fraction < BEEP_SECONDS:
            frequency = BASE_HZ + STEP_HZ * (int(moment) % CODES)
            value += 0.5 * math.sin(math.pi * fraction / BEEP_SECONDS) * math.sin(2 * math.pi * frequency * fraction)
        level = int(max(-1.0, min(1.0, value)) * 32767)
        stereo[2 * index] = level
        stereo[2 * index + 1] = level
        mono[index] = level
    return stereo.tobytes(), mono.tobytes()


def main():
    output = sys.stdout.buffer
    reference = reference_target()
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM) if reference else None
    if sender:
        sender.setblocking(False)
    start = time.time()
    sample = 0
    pending = []

    while True:
        now = time.time()
        while pending and now >= pending[0][1]:
            if sender:
                try:
                    sender.sendto(pending[0][0], reference)
                except OSError:
                    pass
            pending.pop(0)

        due = start + sample / RATE

        if now - due > RESYNC_SECONDS:
            sample = int((now - start) * RATE)
            continue

        if due - now > LEAD_SECONDS:
            wake = due - LEAD_SECONDS
            if pending:
                wake = min(wake, pending[0][1])
            time.sleep(max(0.0, wake - now))
            continue

        stereo, mono = render(start, sample)

        try:
            output.write(stereo)
            output.flush()
        except BrokenPipeError:
            return

        pending.append((mono, due + CHUNK / RATE))
        del pending[:-8]
        sample += CHUNK


if __name__ == "__main__":
    main()
