declare class AudioWorkletProcessor {
  readonly port: MessagePort;
}

declare function registerProcessor(name: string, processor: new () => AudioWorkletProcessor): void;

interface Chunk {
  timestamp: number;
  channels: Float32Array[];
}

interface Incoming {
  type: string;
  timestamp?: number;
  channels?: Float32Array[];
  target?: number;
}

const RATE = 48000;
const REPORT_FRAMES = 2400;
const SLACK = RATE / 10;

class CaligoMoqPlayer extends AudioWorkletProcessor {
  private queue: Chunk[] = [];
  private offset = 0;
  private buffered = 0;
  private target = RATE / 2;
  private playing = false;
  private position = -1;
  private sinceReport = 0;
  private dropped = 0;

  constructor() {
    super();

    this.port.onmessage = (event: MessageEvent<Incoming>) => this.receive(event.data);
  }

  private receive(message: Incoming): void {
    if (message.type === 'chunk' && message.channels && message.timestamp !== undefined) {
      const last = this.queue[this.queue.length - 1];

      if (last && message.timestamp < last.timestamp) {
        this.reset();
      }

      this.queue.push({ timestamp: message.timestamp, channels: message.channels });
      this.buffered += message.channels[0].length;
      this.trim();
    } else if (message.type === 'target' && message.target) {
      this.target = message.target;
      this.trim();
    } else if (message.type === 'reset') {
      this.reset();
    }
  }

  private reset(): void {
    this.queue = [];
    this.offset = 0;
    this.buffered = 0;
    this.playing = false;
    this.position = -1;
  }

  private trim(): void {
    if (this.buffered <= this.target + SLACK) {
      return;
    }

    let excess = this.buffered - this.target;

    while (excess > 0 && this.queue.length > 0) {
      const head = this.queue[0];
      const take = Math.min(head.channels[0].length - this.offset, excess);

      this.offset += take;
      this.buffered -= take;
      this.dropped += take;
      excess -= take;

      if (this.offset >= head.channels[0].length) {
        this.queue.shift();
        this.offset = 0;
      }
    }
  }

  process(_inputs: Float32Array[][], outputs: Float32Array[][]): boolean {
    const output = outputs[0];
    const frames = output[0].length;
    let written = 0;

    if (!this.playing && this.buffered >= this.target) {
      this.playing = true;
    }

    if (this.playing) {
      while (written < frames && this.queue.length > 0) {
        const head = this.queue[0];
        const take = Math.min(head.channels[0].length - this.offset, frames - written);

        for (let channel = 0; channel < output.length; channel++) {
          const source = head.channels[Math.min(channel, head.channels.length - 1)];

          output[channel].set(source.subarray(this.offset, this.offset + take), written);
        }

        this.offset += take;
        this.position = head.timestamp + this.offset;
        this.buffered -= take;
        written += take;

        if (this.offset >= head.channels[0].length) {
          this.queue.shift();
          this.offset = 0;
        }
      }

      if (written < frames) {
        this.playing = false;
        this.port.postMessage({ type: 'underrun' });
      }
    }

    for (let channel = 0; channel < output.length; channel++) {
      output[channel].fill(0, written);
    }

    this.sinceReport += frames;

    if (this.sinceReport >= REPORT_FRAMES) {
      this.sinceReport = 0;
      this.port.postMessage({
        type: 'position',
        position: this.position,
        buffered: this.buffered,
        playing: this.playing,
        dropped: this.dropped
      });
    }

    return true;
  }
}

registerProcessor('caligo-moq-player', CaligoMoqPlayer);
