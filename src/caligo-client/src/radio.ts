export type RadioState = 'off' | 'starting' | 'playing' | 'buffering' | 'retrying';

export type RadioPath = 'moq' | 'opus' | 'relay' | 'direct';

export type RadioFormat = 'mp3' | 'opus' | 'direct';

export interface NowPlaying {
  file: string;
  artist: string;
  album: string;
  title: string;
  isRequest: boolean;
}

interface Source {
  path: RadioPath;
  audio: string;
  events: string;
}

const LOOSE = /"playing":\s*"(.*)",\s*"artist":\s*"(.*)",\s*"album":\s*"(.*)",\s*"title":\s*"(.*)",\s*"bitrate"/s;

export const parseNowPlaying = (raw: string): NowPlaying | null => {
  try {
    const data = JSON.parse(raw) as Record<string, unknown> | null;

    if (!data || typeof data.title !== 'string') {
      return null;
    }

    return {
      file: typeof data.playing === 'string' ? data.playing : '',
      artist: typeof data.artist === 'string' ? data.artist : '',
      album: typeof data.album === 'string' ? data.album : '',
      title: data.title,
      isRequest: data.is_request === true
    };
  } catch {
    const match = LOOSE.exec(raw);

    return match
      ? { file: match[1], artist: match[2], album: match[3], title: match[4], isRequest: /"is_request":\s*true/.test(raw) }
      : null;
  }
};

export const trackLabel = (now: NowPlaying): string =>
  now.artist && now.artist !== 'Unknown' ? `${now.artist} — ${now.title}` : now.title;

let opusSupport: boolean | undefined;

export const opusPlayable = (): boolean => {
  if (opusSupport === undefined) {
    try {
      opusSupport = document.createElement('audio').canPlayType('audio/ogg; codecs="opus"') !== '';
    } catch {
      opusSupport = false;
    }
  }

  return opusSupport;
};

const OPUS: Source = { path: 'opus', audio: '/live/opus', events: '/live/opus/events' };
const RELAY: Source = { path: 'relay', audio: '/live/', events: '/live/events' };
const DIRECT: Source = { path: 'direct', audio: '/radio/', events: '/radio/events' };

const TICK_MS = 1000;
const BUFFERING_MS = 2500;
const STALL_MS = 15000;
const MAX_RETRY_MS = 15000;

export class RadioEngine {
  private audio: HTMLAudioElement | null = null;
  private events: EventSource | null = null;
  private eventsUrl = '';
  private wanted = false;
  private format: RadioFormat = 'mp3';
  private volume = 1;
  private attempts = 0;
  private source = 0;
  private lastTime = 0;
  private lastProgress = 0;
  private played = false;
  private retryTimer: number | undefined;
  private watchdog: number | undefined;
  private readonly onState: (state: RadioState) => void;
  private readonly onNow: (now: NowPlaying | null) => void;
  private readonly onPath: (path: RadioPath) => void;

  constructor(
    onState: (state: RadioState) => void,
    onNow: (now: NowPlaying | null) => void,
    onPath: (path: RadioPath) => void
  ) {
    this.onState = onState;
    this.onNow = onNow;
    this.onPath = onPath;
  }

  start(): void {
    if (this.wanted) {
      return;
    }

    this.wanted = true;
    this.attempts = 0;
    this.source = 0;

    if (!this.audio) {
      this.audio = new Audio();
      this.audio.preload = 'none';
      this.audio.volume = this.volume;
      this.audio.addEventListener('error', () => this.retry());
      this.audio.addEventListener('ended', () => this.retry());
      this.audio.addEventListener('playing', () => this.progressed());
    }

    this.onState('starting');
    this.connect();

    this.watchdog = window.setInterval(() => this.check(), TICK_MS);
  }

  stop(): void {
    this.wanted = false;

    window.clearTimeout(this.retryTimer);
    window.clearInterval(this.watchdog);

    this.retryTimer = undefined;

    this.events?.close();
    this.events = null;
    this.eventsUrl = '';

    if (this.audio) {
      this.audio.pause();
      this.audio.removeAttribute('src');
      this.audio.load();
    }

    this.onNow(null);
    this.onState('off');
  }

  setFormat(format: RadioFormat): void {
    if (format === this.format) {
      return;
    }

    this.format = format;

    if (!this.wanted) {
      return;
    }

    window.clearTimeout(this.retryTimer);

    this.retryTimer = undefined;
    this.attempts = 0;
    this.source = 0;

    this.onState('starting');
    this.connect();
  }

  setVolume(volume: number): void {
    this.volume = volume;

    if (this.audio) {
      this.audio.volume = volume;
    }
  }

  private sources(): Source[] {
    if (this.format === 'direct') {
      return [DIRECT];
    }

    return this.format === 'opus' && opusPlayable() ? [OPUS, RELAY, DIRECT] : [RELAY, DIRECT];
  }

  private connect(): void {
    if (!this.wanted || !this.audio) {
      return;
    }

    const sources = this.sources();
    const source = sources[this.source % sources.length];

    this.lastTime = 0;
    this.lastProgress = Date.now();
    this.played = false;

    this.onPath(source.path);
    this.listen(source.events);

    this.audio.src = `${source.audio}?t=${Date.now()}`;
    this.audio.play().catch(() => undefined);
  }

  private listen(url: string): void {
    if (this.events && this.eventsUrl === url && this.events.readyState !== EventSource.CLOSED) {
      return;
    }

    this.events?.close();
    this.eventsUrl = url;
    this.events = new EventSource(url);
    this.events.onmessage = event => {
      const now = parseNowPlaying(String(event.data));

      if (now) {
        this.onNow(now);
      }
    };
  }

  private check(): void {
    if (!this.wanted || !this.audio || this.retryTimer !== undefined) {
      return;
    }

    const silent = Date.now() - this.lastProgress;

    if (this.audio.currentTime > this.lastTime) {
      this.lastTime = this.audio.currentTime;
      this.attempts = 0;
      this.progressed();
    } else if (silent > STALL_MS) {
      this.retry();
    } else if (this.played && silent > BUFFERING_MS) {
      this.onState('buffering');
    }
  }

  private progressed(): void {
    if (!this.wanted || this.retryTimer !== undefined) {
      return;
    }

    this.lastProgress = Date.now();
    this.played = true;
    this.onState('playing');
  }

  private retry(): void {
    if (!this.wanted || this.retryTimer !== undefined) {
      return;
    }

    if (!this.played) {
      this.source += 1;
    }

    this.attempts += 1;
    this.onState('retrying');

    const delay = Math.min(500 * 2 ** (this.attempts - 1), MAX_RETRY_MS);

    this.retryTimer = window.setTimeout(() => {
      this.retryTimer = undefined;
      this.connect();
    }, delay);
  }
}
