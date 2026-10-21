import workletUrl from '../radio/moq/worklet.ts?worker&url';
import type { Way } from './ways';

export type PlayerState = 'off' | 'starting' | 'playing' | 'buffering' | 'failed';

export interface PlayerStatus {
  state: PlayerState;
  detail?: string;
}

export type Report = (status: PlayerStatus) => void;

export interface WayPlayer {
  start: () => void;
  stop: () => void;
  setVolume: (volume: number) => void;
}

type Fail = (detail: string) => void;
type Load = (audio: HTMLAudioElement, fail: Fail) => Promise<(() => void) | undefined>;

const RATE = 48000;
const MIN_TARGET_MS = 300;
const MAX_TARGET_MS = 2000;
const TARGET_STEP_MS = 100;
const ACTIVE_WAIT_MS = 10000;
const GATHER_MS = 2000;
const BROADCAST = 'caligo/radio/main';

const errorText = (error: unknown): string => (error instanceof Error ? error.message : String(error));

const sleep = (ms: number): Promise<void> => new Promise(resolve => window.setTimeout(resolve, ms));

class MediaPlayer implements WayPlayer {
  private audio: HTMLAudioElement | null = null;
  private release: (() => void) | null = null;
  private volume = 1;
  private readonly report: Report;
  private readonly load: Load;

  constructor(report: Report, load: Load) {
    this.report = report;
    this.load = load;
  }

  start(): void {
    const audio = new Audio();
    const current = () => this.audio === audio;

    audio.volume = this.volume;
    const fail = (detail: string) => {
      if (current()) {
        this.stop();
        this.report({ state: 'failed', detail });
      }
    };

    audio.addEventListener('playing', () => {
      if (current()) this.report({ state: 'playing' });
    });
    audio.addEventListener('waiting', () => {
      if (current()) this.report({ state: 'buffering' });
    });
    audio.addEventListener('error', () => fail(audio.error?.message || 'the browser could not play this stream'));

    this.audio = audio;
    this.report({ state: 'starting' });

    this.load(audio, fail)
      .then(release => {
        if (!current()) {
          release?.();

          return;
        }

        this.release = release ?? null;

        return audio.play();
      })
      .catch(error => fail(errorText(error)));
  }

  stop(): void {
    const audio = this.audio;
    const release = this.release;

    this.audio = null;
    this.release = null;
    release?.();

    if (audio) {
      audio.pause();
      audio.srcObject = null;
      audio.removeAttribute('src');
      audio.load();
    }

    this.report({ state: 'off' });
  }

  setVolume(volume: number): void {
    this.volume = volume;

    if (this.audio) {
      this.audio.volume = volume;
    }
  }
}

const loadHls = async (audio: HTMLAudioElement, url: string, lowLatency: boolean, fail: Fail) => {
  const { default: Hls } = await import('hls.js');

  if (!Hls.isSupported()) {
    if (audio.canPlayType('application/vnd.apple.mpegurl')) {
      audio.src = url;

      return;
    }

    throw new Error('this browser cannot play HLS');
  }

  const hls = new Hls({ lowLatencyMode: lowLatency });

  hls.on(Hls.Events.ERROR, (_event, data) => {
    if (data.fatal) fail(`hls.js: ${data.details}`);
  });
  hls.loadSource(url);
  hls.attachMedia(audio);

  return () => hls.destroy();
};

const loadDash = async (audio: HTMLAudioElement, url: string, fail: Fail) => {
  const dashjs = await import('dashjs');
  const player = dashjs.MediaPlayer().create();

  player.on(dashjs.MediaPlayer.events.ERROR, () => fail('dash.js could not play the stream'));
  player.initialize(audio, url, true);

  return () => player.reset();
};

const gathered = (connection: RTCPeerConnection): Promise<void> =>
  new Promise(resolve => {
    if (connection.iceGatheringState === 'complete') {
      resolve();

      return;
    }

    connection.addEventListener('icegatheringstatechange', () => {
      if (connection.iceGatheringState === 'complete') resolve();
    });
    window.setTimeout(resolve, GATHER_MS);
  });

const loadWhep = async (audio: HTMLAudioElement, url: string, fail: Fail) => {
  const connection = new RTCPeerConnection();

  try {
    const stream = new Promise<MediaStream>(resolve =>
      connection.addEventListener('track', event => resolve(event.streams[0] ?? new MediaStream([event.track])))
    );

    connection.addTransceiver('audio', { direction: 'recvonly' });
    connection.addEventListener('connectionstatechange', () => {
      if (connection.connectionState === 'failed') fail('the WebRTC connection failed');
    });

    await connection.setLocalDescription(await connection.createOffer());
    await gathered(connection);

    const answer = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: connection.localDescription?.sdp
    });

    if (!answer.ok) {
      throw new Error(`WHEP answered ${answer.status}`);
    }

    await connection.setRemoteDescription({ type: 'answer', sdp: await answer.text() });

    audio.srcObject = await stream;

    return () => connection.close();
  } catch (error) {
    connection.close();
    throw error;
  }
};

class MoqLitePlayer implements WayPlayer {
  private context: AudioContext | null = null;
  private node: AudioWorkletNode | null = null;
  private gain: GainNode | null = null;
  private volume = 1;
  private decoder: AudioDecoder | null = null;
  private closers: (() => void)[] = [];
  private targetMs = MIN_TARGET_MS;
  private lastInput = -1;
  private state: PlayerState = 'off';
  private transport = '';
  private readonly url: string;
  private readonly webTransport: boolean;
  private readonly report: Report;

  constructor(url: string, webTransport: boolean, report: Report) {
    this.url = url;
    this.webTransport = webTransport;
    this.report = report;
  }

  start(): void {
    const context = new AudioContext({ sampleRate: RATE, latencyHint: 'playback' });

    this.context = context;
    this.targetMs = MIN_TARGET_MS;
    this.lastInput = -1;
    this.transport = '';
    this.set('starting');

    this.run(context).catch(error => {
      if (this.context === context) {
        this.stop();
        this.report({ state: 'failed', detail: errorText(error) });
      }
    });
  }

  stop(): void {
    for (const close of this.closers.splice(0)) {
      try {
        close();
      } catch {
        continue;
      }
    }

    if (this.decoder && this.decoder.state !== 'closed') {
      this.decoder.close();
    }

    this.decoder = null;
    this.node?.disconnect();
    this.node = null;
    this.gain?.disconnect();
    this.gain = null;
    this.context?.close().catch(() => undefined);
    this.context = null;
    this.set('off');
  }

  setVolume(volume: number): void {
    this.volume = volume;

    if (this.gain) {
      this.gain.gain.value = volume;
    }
  }

  private set(state: PlayerState): void {
    if (state === this.state && state !== 'starting') {
      return;
    }

    this.state = state;
    this.report({ state, detail: this.transport || undefined });
  }

  private async run(context: AudioContext): Promise<void> {
    await context.audioWorklet.addModule(workletUrl);

    if (this.context !== context) {
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

    node.port.onmessage = (event: MessageEvent<{ type: string; playing?: boolean }>) => this.fromWorklet(event.data);
    node.connect(gain);
    this.node = node;
    this.gain = gain;
    this.setTarget(this.targetMs);
    context.resume().catch(() => undefined);

    const Moq = await import('@moq/net');

    if (this.context !== context) {
      return;
    }

    const origin = new Moq.Origin.Producer();

    this.closers.push(() => origin.close());

    const connection = await Moq.Connection.connect({
      url: new URL(this.url),
      consume: origin,
      websocket: this.webTransport ? { enabled: false } : { delay: Moq.Time.Milli(0) }
    });

    this.closers.push(() => connection.close());

    if (this.context !== context) {
      return;
    }

    this.transport = `${connection.transport}, ${connection.version}`;

    const request = origin.request(Moq.Path.from(BROADCAST));

    this.closers.push(() => request.close());

    const deadline = Date.now() + ACTIVE_WAIT_MS;
    let active = request.active.peek();

    while (!active && Date.now() < deadline && this.context === context) {
      await Promise.race([request.active.changed(), sleep(250)]);
      active = request.active.peek();
    }

    if (!active) {
      throw new Error('the relay is not broadcasting the radio');
    }

    const track = active.track('audio/hi').subscribe({ priority: 0 });

    this.closers.push(() => track.close());

    const decoder = new AudioDecoder({
      output: frame => this.output(frame),
      error: error => {
        if (this.context === context) {
          this.stop();
          this.report({ state: 'failed', detail: errorText(error) });
        }
      }
    });

    decoder.configure({ codec: 'opus', sampleRate: RATE, numberOfChannels: 2 });
    this.decoder = decoder;

    let newest = -1;

    while (this.context === context) {
      const group = await track.recvGroup();

      if (!group) {
        throw new Error('the relay ended the stream');
      }

      if (group.sequence < newest) {
        continue;
      }

      newest = group.sequence;

      for (;;) {
        const frame = await group.readFrame();

        if (!frame || this.context !== context) {
          break;
        }

        this.decode(Math.round(frame.timestamp.asMicros()), frame.payload);
      }
    }
  }

  private decode(micros: number, packet: Uint8Array): void {
    const decoder = this.decoder;

    if (!decoder || decoder.state !== 'configured' || micros <= this.lastInput) {
      return;
    }

    this.lastInput = micros;
    decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp: micros, data: packet }));
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

  private setTarget(targetMs: number): void {
    this.targetMs = Math.min(MAX_TARGET_MS, Math.max(MIN_TARGET_MS, targetMs));
    this.node?.port.postMessage({ type: 'target', target: Math.round((this.targetMs * RATE) / 1000) });
  }

  private fromWorklet(message: { type: string; playing?: boolean }): void {
    if (message.type === 'underrun') {
      this.setTarget(this.targetMs + TARGET_STEP_MS);
      this.set('buffering');
    } else if (message.type === 'position' && message.playing) {
      this.set('playing');
    }
  }
}

export const createPlayer = (way: Way, report: Report): WayPlayer | null => {
  switch (way.kind) {
    case 'audio':
      return new MediaPlayer(report, async audio => {
        audio.src = way.url;

        return undefined;
      });
    case 'hls':
      return new MediaPlayer(report, (audio, fail) => loadHls(audio, way.url, way.lowLatency === true, fail));
    case 'dash':
      return new MediaPlayer(report, (audio, fail) => loadDash(audio, way.url, fail));
    case 'whep':
      return new MediaPlayer(report, (audio, fail) => loadWhep(audio, way.url, fail));
    case 'moq-lite':
      return new MoqLitePlayer(way.url, way.webTransport === true, report);
    default:
      return null;
  }
};
