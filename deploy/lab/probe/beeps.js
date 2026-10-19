const BASE_HZ = 1000;
const STEP_HZ = 200;
const CODES = 16;
const WINDOW_SECONDS = 0.05;
const ONSET_RATIO = 4;
const QUIET_SECONDS = 0.5;

class BeepDetector extends AudioWorkletProcessor {
  constructor() {
    super();
    this.window = Math.round(sampleRate * WINDOW_SECONDS);
    this.buffer = new Float32Array(this.window);
    this.filled = 0;
    this.collecting = false;
    this.onsetFrame = 0;
    this.quietUntil = 0;
    this.floor = 0;
    this.heard = false;
  }

  goertzel(frequency) {
    const omega = (2 * Math.PI * frequency) / sampleRate;
    const coefficient = 2 * Math.cos(omega);
    let previous = 0;
    let before = 0;

    for (let index = 0; index < this.window; index++) {
      const current = this.buffer[index] + coefficient * previous - before;

      before = previous;
      previous = current;
    }

    return previous * previous + before * before - coefficient * previous * before;
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];

    if (!channel) {
      return true;
    }

    let energy = 0;

    for (let index = 0; index < channel.length; index++) {
      energy += channel[index] * channel[index];
    }

    energy /= channel.length;

    if (!this.heard && energy > 1e-7) {
      this.heard = true;
      this.port.postMessage({ kind: 'audio', frame: currentFrame });
    }

    if (!this.collecting) {
      if (energy > this.floor * ONSET_RATIO && energy > 0.005 && currentFrame >= this.quietUntil) {
        this.collecting = true;
        this.onsetFrame = currentFrame;
        this.filled = 0;
      }
      else {
        this.floor = this.floor * 0.95 + energy * 0.05;
      }
    }

    if (this.collecting) {
      const take = Math.min(channel.length, this.window - this.filled);

      this.buffer.set(channel.subarray(0, take), this.filled);
      this.filled += take;

      if (this.filled === this.window) {
        let best = 0;
        let bestPower = -1;

        for (let code = 0; code < CODES; code++) {
          const power = this.goertzel(BASE_HZ + STEP_HZ * code);

          if (power > bestPower) {
            bestPower = power;
            best = code;
          }
        }

        this.port.postMessage({ kind: 'beep', code: best, frame: this.onsetFrame });
        this.collecting = false;
        this.quietUntil = this.onsetFrame + Math.round(sampleRate * QUIET_SECONDS);
      }
    }

    return true;
  }
}

registerProcessor('beep-detector', BeepDetector);
