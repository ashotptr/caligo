import workletUrl from './worklet.ts?worker&url';
import {
  MOQ_PROTOCOL,
  PROBE,
  TRACK_AUDIO,
  TRACK_CATALOG,
  TRACK_META,
  parseAudio,
  parseJson,
  parseObject,
  parseProbe,
  probeMessage
} from './objects';
import type { Catalog, MetaObject } from './objects';
import type { NowPlaying, RadioPath, RadioState } from '../../radio';

const RATE = 48000;
const MIN_TARGET_MS = 300;
const MAX_TARGET_MS = 2000;
const TARGET_STEP_MS = 100;
const TARGET_RELAX_MS = 10000;
const JOIN_SETTLE_MS = 5000;
const PROBE_EVERY_MS = 2000;
const MAX_ROUND_TRIP_MS = 2000;
const STALL_MS = 8000;
const GIVE_UP_MS = 10000;
const MAX_RETRY_MS = 15000;
const MAX_LAG_MS = 2000;
const MAX_PACKET_SAMPLES = 5760;
const REJOIN_GAP_MS = 3000;
const ARRIVAL_GAP_MS = 1000;

interface WorkletMessage {
  type: string;
  position?: number;
  playing?: boolean;
}

export const moqPlayable = async (): Promise<boolean> => {
  if (typeof AudioDecoder === 'undefined' || typeof AudioWorkletNode === 'undefined' || typeof WebSocket === 'undefined') {
    return false;
  }

  try {
    const support = await AudioDecoder.isConfigSupported({ codec: 'opus', sampleRate: RATE, numberOfChannels: 2 });

    return support.supported === true;
  } catch {
    return false;
  }
};

const nowPlaying = (meta: MetaObject): NowPlaying => ({
  file: '',
  artist: meta.artist,
  album: meta.album,
  title: meta.title,
  isRequest: meta.is_request
});

export class MoqRadio {
  private wanted = false;
  private socket: WebSocket | null = null;
  private context: AudioContext | null = null;
  private node: AudioWorkletNode | null = null;
  private gain: GainNode | null = null;
  private volume = 1;
  private decoder: AudioDecoder | null = null;
  private channels = 0;
  private catalogChannels = 2;
  private lastInput = -1;
  private pending: MetaObject[] = [];
  private position = -1;
  private targetMs = MIN_TARGET_MS;
  private lastUnderrun = 0;
  private lastProbe = 0;
  private lastAudio = 0;
  private earliest = Number.POSITIVE_INFINITY;
  private lastRejoin = 0;
  private joinedAt = 0;
  private offAirSince = 0;
  private attempts = 0;
  private retryTimer: number | undefined;
  private watchdog: number | undefined;
  private readonly onState: (state: RadioState) => void;
  private readonly onNow: (now: NowPlaying | null) => void;
  private readonly onPath: (path: RadioPath) => void;
  private readonly onGiveUp: () => void;

  constructor(
    onState: (state: RadioState) => void,
    onNow: (now: NowPlaying | null) => void,
    onPath: (path: RadioPath) => void,
    onGiveUp: () => void
  ) {
    this.onState = onState;
    this.onNow = onNow;
    this.onPath = onPath;
    this.onGiveUp = onGiveUp;
  }

  start(): void {
    if (this.wanted) {
      return;
    }

    this.wanted = true;
    this.attempts = 0;
    this.position = -1;
    this.pending = [];
    this.lastAudio = Date.now();
    this.offAirSince = 0;
    this.targetMs = MIN_TARGET_MS;
    this.lastUnderrun = 0;
    this.lastRejoin = 0;
    this.earliest = Number.POSITIVE_INFINITY;

    this.onPath('moq');
    this.onState('starting');

    const context = new AudioContext({ sampleRate: RATE, latencyHint: 'playback' });

    this.context = context;

    context.audioWorklet
      .addModule(workletUrl)
      .then(() => {
        if (!this.wanted || this.context !== context) {
          return;
        }

        const node = new AudioWorkletNode(context, 'caligo-moq-player', {
          numberOfInputs: 0,
          numberOfOutputs: 1,
          outputChannelCount: [2]
        });

        const gain = context.createGain();

        gain.gain.value = this.volume;
        gain.connect(context.destination);

        node.port.onmessage = (event: MessageEvent<WorkletMessage>) => this.fromWorklet(event.data);
        node.connect(gain);
        node.port.postMessage({ type: 'target', target: Math.round((this.targetMs * RATE) / 1000) });

        this.node = node;
        this.gain = gain;

        context.resume().catch(() => undefined);
        this.connect();
      })
      .catch(() => this.giveUp());

    this.watchdog = window.setInterval(() => this.check(), 1000);
  }

  stop(): void {
    this.wanted = false;

    window.clearTimeout(this.retryTimer);
    window.clearInterval(this.watchdog);

    this.retryTimer = undefined;
    this.watchdog = undefined;

    this.closeSocket();
    this.closeDecoder();

    this.node?.disconnect();
    this.node = null;
    this.gain?.disconnect();
    this.gain = null;

    this.context?.close().catch(() => undefined);
    this.context = null;

    this.onNow(null);
    this.onState('off');
  }

  setVolume(volume: number): void {
    this.volume = volume;

    if (this.gain) {
      this.gain.gain.value = volume;
    }
  }

  private giveUp(): void {
    if (!this.wanted) {
      return;
    }

    this.stop();
    this.onGiveUp();
  }

  private connect(): void {
    if (!this.wanted) {
      return;
    }

    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    const socket = new WebSocket(`${scheme}://${window.location.host}/live/moq`, MOQ_PROTOCOL);

    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
      this.joinedAt = Date.now();
    };
    socket.onmessage = (event: MessageEvent<ArrayBuffer>) => this.receive(event.data);
    socket.onclose = () => {
      if (this.socket === socket) {
        this.socket = null;
        this.retry();
      }
    };

    this.socket = socket;
  }

  private closeSocket(): void {
    const socket = this.socket;

    this.socket = null;
    socket?.close();
  }

  private closeDecoder(): void {
    if (this.decoder && this.decoder.state !== 'closed') {
      this.decoder.close();
    }

    this.decoder = null;
    this.channels = 0;
    this.lastInput = -1;
  }

  private retry(): void {
    if (!this.wanted || this.retryTimer !== undefined) {
      return;
    }

    this.attempts += 1;
    this.onState('retrying');

    this.retryTimer = window.setTimeout(() => {
      this.retryTimer = undefined;
      this.connect();
    }, Math.min(500 * 2 ** (this.attempts - 1), MAX_RETRY_MS));
  }

  private check(): void {
    if (!this.wanted) {
      return;
    }

    const now = Date.now();

    if (this.offAirSince > 0 && now - this.offAirSince > GIVE_UP_MS) {
      this.giveUp();

      return;
    }

    if (this.socket && this.socket.readyState === WebSocket.OPEN && now - this.lastAudio > STALL_MS) {
      this.lastAudio = now;
      this.closeSocket();
      this.retry();

      return;
    }

    if (this.socket && this.socket.readyState === WebSocket.OPEN && now - this.lastProbe >= PROBE_EVERY_MS) {
      this.lastProbe = now;
      this.socket.send(probeMessage(performance.now()));
    }

    if (this.targetMs > MIN_TARGET_MS && now - this.lastUnderrun > TARGET_RELAX_MS) {
      this.lastUnderrun = now;
      this.setTarget(this.targetMs - TARGET_STEP_MS);
    }
  }

  private setTarget(targetMs: number): void {
    this.targetMs = Math.min(MAX_TARGET_MS, Math.max(MIN_TARGET_MS, targetMs));
    this.node?.port.postMessage({ type: 'target', target: Math.round((this.targetMs * RATE) / 1000) });
  }

  private rejoin(): void {
    this.lastRejoin = Date.now();
    this.earliest = Number.POSITIVE_INFINITY;
    this.restartDecoder();
    this.node?.port.postMessage({ type: 'reset' });
    this.closeSocket();
    this.connect();
  }

  private restartDecoder(): void {
    this.lastInput = -1;

    if (this.decoder && this.decoder.state === 'configured') {
      this.decoder.reset();
      this.decoder.configure({ codec: 'opus', sampleRate: RATE, numberOfChannels: this.channels });
    }
  }

  private receive(data: ArrayBuffer): void {
    if (new Uint8Array(data, 0, Math.min(1, data.byteLength))[0] === PROBE) {
      const probe = parseProbe(data);

      if (probe && performance.now() - probe.sent > MAX_ROUND_TRIP_MS && Date.now() - this.lastRejoin >= REJOIN_GAP_MS) {
        this.rejoin();
      }

      return;
    }

    const object = parseObject(data);

    if (!object) {
      return;
    }

    if (object.track === TRACK_CATALOG) {
      const catalog = parseJson<Catalog>(object.payload);

      if (catalog) {
        this.configure(catalog);
      }
    } else if (object.track === TRACK_META) {
      const meta = parseJson<MetaObject>(object.payload);

      if (meta) {
        this.queueMeta(meta);
      }
    } else if (object.track === TRACK_AUDIO) {
      const audio = parseAudio(object.payload);

      if (audio && !this.fallingBehind(audio.timestamp)) {
        this.decode(audio.timestamp, audio.packet);
      }
    }
  }

  private fallingBehind(timestamp: number): boolean {
    const now = Date.now();
    const offset = now - (timestamp * 1000) / RATE;

    if (now - this.lastAudio > ARRIVAL_GAP_MS) {
      this.earliest = offset;
    }

    this.lastAudio = now;
    this.earliest = Math.min(this.earliest, offset);

    if (offset - this.earliest <= MAX_LAG_MS || now - this.lastRejoin < REJOIN_GAP_MS) {
      return false;
    }

    this.rejoin();

    return true;
  }

  private configure(catalog: Catalog): void {
    this.offAirSince = catalog.on_air ? 0 : this.offAirSince || Date.now();

    const audio = catalog.tracks.find(track => track.name === 'audio/hi');

    this.catalogChannels = audio?.channels ?? 2;
    this.ensureDecoder();
  }

  private ensureDecoder(): void {
    const channels = this.catalogChannels;

    if (this.decoder && channels === this.channels) {
      return;
    }

    this.closeDecoder();
    this.channels = channels;
    this.node?.port.postMessage({ type: 'reset' });

    const decoder = new AudioDecoder({
      output: frame => this.output(frame),
      error: () => {
        if (this.decoder === decoder) {
          this.decoder = null;
          this.channels = 0;
        }
      }
    });

    decoder.configure({ codec: 'opus', sampleRate: RATE, numberOfChannels: channels });

    this.decoder = decoder;
  }

  private decode(timestamp: number, packet: Uint8Array): void {
    if (!this.decoder) {
      this.ensureDecoder();
    }

    if (!this.decoder || this.decoder.state !== 'configured') {
      return;
    }

    if (this.lastInput >= 0 && (timestamp <= this.lastInput || timestamp - this.lastInput > MAX_PACKET_SAMPLES)) {
      this.restartDecoder();
    }

    this.lastInput = timestamp;

    this.decoder.decode(
      new EncodedAudioChunk({ type: 'key', timestamp: Math.round((timestamp * 1e6) / RATE), data: packet })
    );
  }

  private output(frame: AudioData): void {
    const frames = frame.numberOfFrames;
    const timestamp = Math.round((frame.timestamp * RATE) / 1e6);
    const channels: Float32Array[] = [];

    for (let channel = 0; channel < frame.numberOfChannels; channel++) {
      const plane = new Float32Array(frames);

      frame.copyTo(plane, { planeIndex: channel, format: 'f32-planar' });
      channels.push(plane);
    }

    frame.close();

    this.node?.port.postMessage({ type: 'chunk', timestamp, channels }, channels.map(plane => plane.buffer));
  }

  private queueMeta(meta: MetaObject): void {
    if (this.position < 0 || meta.at <= this.position) {
      this.onNow(nowPlaying(meta));

      return;
    }

    this.pending.push(meta);
  }

  private fromWorklet(message: WorkletMessage): void {
    if (message.type === 'underrun') {
      if (this.wanted && this.retryTimer === undefined) {
        if (Date.now() - this.joinedAt > JOIN_SETTLE_MS) {
          this.lastUnderrun = Date.now();
          this.setTarget(this.targetMs + TARGET_STEP_MS);
        }

        this.onState('buffering');
      }

      return;
    }

    if (message.type !== 'position' || message.position === undefined) {
      return;
    }

    this.position = message.position;

    if (message.playing && this.retryTimer === undefined) {
      this.attempts = 0;
      this.onState('playing');
    }

    while (this.pending.length > 0 && this.pending[0].at <= this.position) {
      const meta = this.pending.shift();

      if (meta) {
        this.onNow(nowPlaying(meta));
      }
    }
  }
}
