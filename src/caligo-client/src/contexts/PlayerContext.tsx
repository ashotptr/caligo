import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import api from '../api/axios';
import { createPlayer } from '../listen/players';
import type { PlayerStatus } from '../listen/players';
import { DEFAULT_WAY, allWays } from '../listen/ways';
import type { Way } from '../listen/ways';
import { RadioEngine, opusPlayable, parseNowPlaying } from '../radio';
import type { NowPlaying, RadioPath, RadioState } from '../radio';
import { MoqRadio, moqPlayable } from '../radio/moq/player';
import { useToast } from './ToastContext';

export type PlayerMode = 'off' | 'radio' | 'track';

export type RadioStatus = RadioState | 'failed';

export interface PlayerTrack {
  contentId: number | null;
  title: string;
  artist: string;
  album: string;
  audioUri: string;
  coverUri?: string | null;
}

interface Driver {
  stop: () => void;
  setVolume: (volume: number) => void;
}

interface PlayerContextType {
  mode: PlayerMode;
  ways: Way[];
  way: Way;
  radioState: RadioStatus;
  radioDetail: string | null;
  path: RadioPath | null;
  now: NowPlaying | null;
  songId: number | null;
  loggedId: number | null;
  canPlayOpus: boolean;
  canPlayMoq: boolean;
  markLogged: (contentId: number) => void;
  playRadio: (wayId?: string) => void;
  stopRadio: () => void;
  queue: PlayerTrack[];
  index: number;
  current: PlayerTrack | null;
  paused: boolean;
  trackFailed: boolean;
  playTracks: (tracks: PlayerTrack[], index: number) => void;
  togglePause: () => void;
  next: () => void;
  previous: () => void;
  seek: (seconds: number) => void;
  volume: number;
  setVolume: (volume: number) => void;
  stop: () => void;
  position: () => number | null;
}

interface PlayerClock {
  time: number;
  duration: number;
}

const WAY_KEY = 'radioWay';
const OLD_FORMAT_KEY = 'radioFormat';
const OLD_FORMATS: Record<string, string> = { mp3: 'relay-mp3', opus: 'relay-opus', moq: 'relay-objects' };
const RESTART_PREVIOUS_S = 3;

const storedWay = (ways: Way[]): Way => {
  const fallback = ways.find(way => way.id === DEFAULT_WAY) ?? ways[0];

  try {
    const id = localStorage.getItem(WAY_KEY) ?? OLD_FORMATS[localStorage.getItem(OLD_FORMAT_KEY) ?? ''];

    return ways.find(way => way.id === id && way.kind !== 'external') ?? fallback;
  } catch {
    return fallback;
  }
};

const storeWay = (way: Way): void => {
  try {
    localStorage.setItem(WAY_KEY, way.id);
  } catch {
    return;
  }
};

const fromWay = (status: PlayerStatus): RadioStatus =>
  status.state === 'buffering' && status.detail === 'reconnecting' ? 'retrying' : status.state;

const PHONE = '(hover: none) and (pointer: coarse)';
const SEEK_STEP_S = 10;

const onPhone = (): boolean => {
  try {
    return window.matchMedia(PHONE).matches;
  } catch {
    return false;
  }
};

const webAudio = (candidate: Way): boolean => candidate.kind === 'objects' || candidate.kind === 'moq-lite';

const playbackSession = (): void => {
  const session = (navigator as Navigator & { audioSession?: { type: string } }).audioSession;

  if (!session) {
    return;
  }

  try {
    session.type = 'playback';
  } catch {
    return;
  }
};

const mediaSession = (): MediaSession | null => ('mediaSession' in navigator ? navigator.mediaSession : null);

const known = (value: string | undefined): string => (value && value !== 'Unknown' ? value : '');

const PlayerContext = createContext<PlayerContextType | undefined>(undefined);
const ClockContext = createContext<PlayerClock>({ time: 0, duration: 0 });

export const PlayerProvider = ({ children }: { children: ReactNode }) => {
  const toast = useToast();
  const ways = useMemo(allWays, []);

  const [mode, setMode] = useState<PlayerMode>('off');
  const [way, setWay] = useState<Way>(() => storedWay(ways));
  const [radioState, setRadioState] = useState<RadioStatus>('off');
  const [radioDetail, setRadioDetail] = useState<string | null>(null);
  const [path, setPath] = useState<RadioPath | null>(null);
  const [now, setNow] = useState<NowPlaying | null>(null);
  const [songId, setSongId] = useState<number | null>(null);
  const [loggedId, setLoggedId] = useState<number | null>(null);
  const [canPlayOpus] = useState<boolean>(opusPlayable);
  const [canPlayMoq, setCanPlayMoq] = useState(false);
  const [queue, setQueue] = useState<PlayerTrack[]>([]);
  const [index, setIndex] = useState(-1);
  const [paused, setPaused] = useState(true);
  const [trackFailed, setTrackFailed] = useState(false);
  const [clock, setClock] = useState<PlayerClock>({ time: 0, duration: 0 });
  const [volume, setVolumeState] = useState(1);

  const driver = useRef<Driver | null>(null);
  const events = useRef<EventSource | null>(null);
  const audio = useRef<HTMLAudioElement | null>(null);
  const queueRef = useRef<PlayerTrack[]>([]);
  const indexRef = useRef(-1);
  const volumeRef = useRef(1);
  const advance = useRef<() => void>(() => undefined);
  const modeRef = useRef<PlayerMode>('off');
  const wayRef = useRef<Way>(way);
  const backgrounded = useRef(false);

  const closeEvents = useCallback(() => {
    events.current?.close();
    events.current = null;
  }, []);

  const dropDriver = useCallback(() => {
    const previous = driver.current;

    driver.current = null;
    previous?.stop();
    closeEvents();
  }, [closeEvents]);

  const trackAudio = useCallback((): HTMLAudioElement => {
    if (!audio.current) {
      const element = new Audio();

      element.preload = 'auto';
      element.addEventListener('timeupdate', () => setClock(previous => ({ ...previous, time: element.currentTime })));
      element.addEventListener('durationchange', () =>
        setClock(previous => ({ ...previous, duration: Number.isFinite(element.duration) ? element.duration : 0 })));
      element.addEventListener('play', () => setPaused(false));
      element.addEventListener('pause', () => setPaused(true));
      element.addEventListener('ended', () => advance.current());
      element.addEventListener('error', () => {
        if (element.getAttribute('src')) {
          setTrackFailed(true);
        }
      });

      audio.current = element;
    }

    return audio.current;
  }, []);

  const silenceTrack = useCallback(() => {
    const element = audio.current;

    if (element) {
      element.pause();
      element.removeAttribute('src');
      element.load();
    }

    setPaused(true);
    setClock({ time: 0, duration: 0 });
  }, []);

  const playAt = useCallback((at: number) => {
    const track = queueRef.current[at];

    if (!track) {
      return;
    }

    const element = trackAudio();

    indexRef.current = at;
    setIndex(at);
    setTrackFailed(false);
    setClock({ time: 0, duration: 0 });

    element.src = track.audioUri;
    element.volume = volumeRef.current;
    element.play().catch(() => setPaused(true));

    toast(`Playing: ${track.title}`);
  }, [toast, trackAudio]);

  useEffect(() => {
    advance.current = () => {
      if (indexRef.current < queueRef.current.length - 1) {
        playAt(indexRef.current + 1);
      }
    };
  }, [playAt]);

  const listen = useCallback((url: string, owner: Driver) => {
    closeEvents();

    const source = new EventSource(url);

    source.onmessage = event => {
      const heard = parseNowPlaying(String(event.data));

      if (heard && driver.current === owner) {
        setNow(heard);
      }
    };

    events.current = source;
  }, [closeEvents]);

  const startWay = useCallback(function start(target: Way) {
    dropDriver();

    setNow(null);
    setPath(null);
    setRadioDetail(null);

    if (target.kind === 'stream') {
      const engine: RadioEngine = new RadioEngine(
        state => {
          if (driver.current === engine) setRadioState(state);
        },
        heard => {
          if (driver.current === engine) setNow(heard);
        },
        next => {
          if (driver.current === engine) setPath(next);
        }
      );

      driver.current = engine;
      engine.setFormat(target.format ?? 'mp3');
      engine.setVolume(volumeRef.current);
      engine.start();

      return;
    }

    if (target.kind === 'objects') {
      const radio: MoqRadio = new MoqRadio(
        state => {
          if (driver.current === radio) setRadioState(state);
        },
        heard => {
          if (driver.current === radio) setNow(heard);
        },
        next => {
          if (driver.current === radio) setPath(next);
        },
        () => {
          if (driver.current !== radio) {
            return;
          }

          const fallback = ways.find(candidate => candidate.id === DEFAULT_WAY);

          if (fallback) {
            setWay(fallback);
            storeWay(fallback);
            start(fallback);
          }
        }
      );

      driver.current = radio;
      radio.setVolume(volumeRef.current);
      radio.start();

      return;
    }

    let owner: Driver | null = null;

    const player = createPlayer(target, status => {
      if (owner === null || driver.current !== owner) {
        return;
      }

      setRadioState(fromWay(status));
      setRadioDetail(status.state === 'failed' ? status.detail ?? null : null);
    });

    if (!player) {
      setRadioState('failed');
      setRadioDetail('this way plays outside the browser');

      return;
    }

    owner = player;
    driver.current = player;
    player.setVolume(volumeRef.current);

    if (target.events) {
      listen(target.events, player);
    }

    player.start();
  }, [dropDriver, listen, ways]);

  const playRadio = useCallback((wayId?: string) => {
    const target = wayId ? ways.find(candidate => candidate.id === wayId) : way;

    if (!target || target.kind === 'external') {
      return;
    }

    playbackSession();
    backgrounded.current = false;
    silenceTrack();
    setMode('radio');
    setWay(target);
    storeWay(target);
    startWay(target);
  }, [silenceTrack, startWay, way, ways]);

  const stopRadio = useCallback(() => {
    backgrounded.current = false;
    dropDriver();
    setMode(previous => (previous === 'radio' ? 'off' : previous));
    setRadioState('off');
    setRadioDetail(null);
    setNow(null);
    setPath(null);
  }, [dropDriver]);

  const playTracks = useCallback((tracks: PlayerTrack[], at: number) => {
    if (!tracks[at]) {
      return;
    }

    playbackSession();
    stopRadio();
    queueRef.current = tracks;
    setQueue(tracks);
    setMode('track');
    playAt(at);
  }, [playAt, stopRadio]);

  const togglePause = useCallback(() => {
    const element = audio.current;

    if (mode !== 'track' || !element) {
      return;
    }

    if (element.paused) {
      element.play().catch(() => setPaused(true));
    } else {
      element.pause();
    }
  }, [mode]);

  const next = useCallback(() => {
    if (indexRef.current < queueRef.current.length - 1) {
      playAt(indexRef.current + 1);
    }
  }, [playAt]);

  const previous = useCallback(() => {
    const element = audio.current;

    if (element && (element.currentTime > RESTART_PREVIOUS_S || indexRef.current <= 0)) {
      element.currentTime = 0;

      return;
    }

    playAt(indexRef.current - 1);
  }, [playAt]);

  const seek = useCallback((seconds: number) => {
    const element = audio.current;

    if (element && Number.isFinite(seconds)) {
      element.currentTime = Math.max(0, seconds);
    }
  }, []);

  const setVolume = useCallback((next: number) => {
    const level = Math.min(1, Math.max(0, next));

    volumeRef.current = level;
    setVolumeState(level);
    driver.current?.setVolume(level);

    if (audio.current) {
      audio.current.volume = level;
    }
  }, []);

  const stop = useCallback(() => {
    stopRadio();
    silenceTrack();
    queueRef.current = [];
    indexRef.current = -1;
    setQueue([]);
    setIndex(-1);
    setMode('off');
  }, [silenceTrack, stopRadio]);

  const position = useCallback(
    () => (mode === 'track' && audio.current ? audio.current.currentTime : null),
    [mode]
  );

  useEffect(() => {
    let cancelled = false;

    moqPlayable().then(supported => {
      if (!cancelled) setCanPlayMoq(supported);
    });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => () => {
    const current = driver.current;

    driver.current = null;
    current?.stop();
    events.current?.close();
    audio.current?.pause();
  }, []);

  useEffect(() => {
    modeRef.current = mode;
  }, [mode]);

  useEffect(() => {
    wayRef.current = way;
  }, [way]);

  useEffect(() => {
    const changed = () => {
      const chosen = wayRef.current;

      if (document.visibilityState === 'hidden') {
        if (modeRef.current !== 'radio' || !webAudio(chosen) || !onPhone() || backgrounded.current) {
          return;
        }

        const fallback = ways.find(candidate => candidate.id === DEFAULT_WAY);

        if (fallback) {
          backgrounded.current = true;
          startWay(fallback);
        }
      } else if (backgrounded.current) {
        backgrounded.current = false;

        if (modeRef.current === 'radio') {
          startWay(chosen);
        }
      }
    };

    document.addEventListener('visibilitychange', changed);

    return () => document.removeEventListener('visibilitychange', changed);
  }, [startWay, ways]);

  const current = mode === 'track' ? queue[index] ?? null : null;

  useEffect(() => {
    const session = mediaSession();

    if (!session || typeof MediaMetadata === 'undefined') {
      return;
    }

    if (mode === 'track' && current) {
      session.metadata = new MediaMetadata({
        title: current.title,
        artist: known(current.artist),
        album: known(current.album),
        artwork: current.coverUri ? [{ src: new URL(current.coverUri, window.location.href).href }] : []
      });
    } else if (mode === 'radio') {
      session.metadata = new MediaMetadata({
        title: now?.title || 'Caligo Radio',
        artist: known(now?.artist),
        album: known(now?.album) || way.name
      });
    } else {
      session.metadata = null;
    }
  }, [mode, current, now, way]);

  useEffect(() => {
    const session = mediaSession();

    if (!session) {
      return;
    }

    if (mode === 'track') {
      session.playbackState = paused ? 'paused' : 'playing';
    } else if (mode === 'radio') {
      session.playbackState = radioState === 'off' || radioState === 'failed' ? 'paused' : 'playing';
    } else {
      session.playbackState = 'none';
    }
  }, [mode, paused, radioState]);

  useEffect(() => {
    const session = mediaSession();

    if (!session) {
      return;
    }

    const handle = (action: MediaSessionAction, handler: MediaSessionActionHandler | null) => {
      try {
        session.setActionHandler(action, handler);
      } catch {
        return;
      }
    };

    const position = () => audio.current?.currentTime ?? 0;

    if (mode === 'track') {
      handle('play', () => {
        audio.current?.play().catch(() => setPaused(true));
      });
      handle('pause', () => audio.current?.pause());
      handle('stop', stop);
      handle('previoustrack', previous);
      handle('nexttrack', next);
      handle('seekto', details => {
        if (details.seekTime !== undefined) seek(details.seekTime);
      });
      handle('seekbackward', details => seek(position() - (details.seekOffset ?? SEEK_STEP_S)));
      handle('seekforward', details => seek(position() + (details.seekOffset ?? SEEK_STEP_S)));

      return;
    }

    handle('play', () => playRadio());
    handle('pause', stopRadio);
    handle('stop', stopRadio);
    handle('previoustrack', null);
    handle('nexttrack', null);
    handle('seekto', null);
    handle('seekbackward', null);
    handle('seekforward', null);
  }, [mode, next, previous, seek, stop, playRadio, stopRadio]);

  useEffect(() => {
    const session = mediaSession();

    if (!session || typeof session.setPositionState !== 'function') {
      return;
    }

    try {
      if (mode === 'track' && clock.duration > 0) {
        session.setPositionState({ duration: clock.duration, playbackRate: 1, position: Math.min(clock.time, clock.duration) });
      } else {
        session.setPositionState();
      }
    } catch {
      return;
    }
  }, [mode, clock]);

  const artist = mode === 'radio' ? now?.artist ?? '' : '';
  const title = mode === 'radio' ? now?.title ?? '' : '';

  useEffect(() => {
    setSongId(null);

    if (!artist || !title) {
      return;
    }

    let cancelled = false;

    api
      .get<{ contentId: number }>('/api/songs/lookup', { params: { artist, title } })
      .then(response => {
        if (!cancelled) setSongId(response.data.contentId);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [artist, title]);

  const value = useMemo<PlayerContextType>(() => ({
    mode,
    ways,
    way,
    radioState,
    radioDetail,
    path,
    now,
    songId,
    loggedId,
    canPlayOpus,
    canPlayMoq,
    markLogged: setLoggedId,
    playRadio,
    stopRadio,
    queue,
    index,
    current,
    paused,
    trackFailed,
    playTracks,
    togglePause,
    next,
    previous,
    seek,
    volume,
    setVolume,
    stop,
    position
  }), [
    mode, ways, way, radioState, radioDetail, path, now, songId, loggedId, canPlayOpus, canPlayMoq, playRadio,
    stopRadio, queue, index, current, paused, trackFailed, playTracks, togglePause, next, previous, seek, volume,
    setVolume, stop, position
  ]);

  return (
    <PlayerContext.Provider value={value}>
      <ClockContext.Provider value={clock}>{children}</ClockContext.Provider>
    </PlayerContext.Provider>
  );
};

export const usePlayer = (): PlayerContextType => {
  const context = useContext(PlayerContext);

  if (!context) {
    throw new Error('usePlayer must be used within a PlayerProvider');
  }

  return context;
};

export const usePlayerClock = (): PlayerClock => useContext(ClockContext);
