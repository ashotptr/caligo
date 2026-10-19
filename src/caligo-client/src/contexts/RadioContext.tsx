import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import api from '../api/axios';
import { RadioEngine, opusPlayable } from '../radio';
import type { NowPlaying, RadioFormat, RadioPath, RadioState } from '../radio';
import { MoqRadio, moqPlayable } from '../radio/moq/player';

interface RadioContextType {
  state: RadioState;
  path: RadioPath;
  now: NowPlaying | null;
  songId: number | null;
  loggedId: number | null;
  format: RadioFormat;
  canPlayOpus: boolean;
  canPlayMoq: boolean;
  markLogged: (contentId: number) => void;
  setFormat: (format: RadioFormat) => void;
  start: () => void;
  stop: () => void;
  toggle: () => void;
}

const FORMAT_KEY = 'radioFormat';

const storedFormat = (): RadioFormat => {
  try {
    const stored = localStorage.getItem(FORMAT_KEY);

    return stored === 'opus' || stored === 'moq' ? stored : 'mp3';
  } catch {
    return 'mp3';
  }
};

const storeFormat = (format: RadioFormat): void => {
  try {
    localStorage.setItem(FORMAT_KEY, format);
  } catch {
    return;
  }
};

const lowLatencyChosen = (choice: RadioFormat, supported: boolean): boolean => choice === 'moq' && supported;

const RadioContext = createContext<RadioContextType | undefined>(undefined);

export const RadioProvider = ({ children }: { children: ReactNode }) => {
  const [state, setState] = useState<RadioState>('off');
  const [path, setPath] = useState<RadioPath>('relay');
  const [now, setNow] = useState<NowPlaying | null>(null);
  const [songId, setSongId] = useState<number | null>(null);
  const [loggedId, setLoggedId] = useState<number | null>(null);
  const [format, setFormatState] = useState<RadioFormat>(storedFormat);
  const [canPlayOpus] = useState<boolean>(opusPlayable);
  const [canPlayMoq, setCanPlayMoq] = useState(false);

  const engine = useRef<RadioEngine | null>(null);
  const moq = useRef<MoqRadio | null>(null);
  const formatRef = useRef<RadioFormat>(format);
  const moqRef = useRef(false);
  const playingRef = useRef(false);

  const classic = useCallback(() => {
    if (!engine.current) {
      engine.current = new RadioEngine(setState, setNow, setPath);
      engine.current.setFormat(formatRef.current);
    }

    return engine.current;
  }, []);

  const applyFormat = useCallback((next: RadioFormat) => {
    const wasMoq = lowLatencyChosen(formatRef.current, moqRef.current);

    formatRef.current = next;

    setFormatState(next);
    storeFormat(next);

    engine.current?.setFormat(next);

    if (!playingRef.current || wasMoq === lowLatencyChosen(next, moqRef.current)) {
      return;
    }

    if (wasMoq) {
      moq.current?.stop();
      classic().start();
    } else {
      engine.current?.stop();
      moq.current?.start();
    }
  }, [classic]);

  const lowLatency = useCallback(() => {
    moq.current ??= new MoqRadio(setState, setNow, setPath, () => applyFormat('mp3'));

    return moq.current;
  }, [applyFormat]);

  useEffect(() => {
    let cancelled = false;

    moqPlayable().then(supported => {
      if (!cancelled) {
        moqRef.current = supported;
        setCanPlayMoq(supported);
      }
    });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => () => {
    engine.current?.stop();
    moq.current?.stop();
  }, []);

  const start = useCallback(() => {
    playingRef.current = true;

    if (lowLatencyChosen(formatRef.current, moqRef.current)) {
      lowLatency().start();
    } else {
      classic().start();
    }
  }, [classic, lowLatency]);

  const stop = useCallback(() => {
    playingRef.current = false;

    engine.current?.stop();
    moq.current?.stop();
  }, []);

  const setFormat = useCallback((next: RadioFormat) => {
    lowLatency();
    applyFormat(next);
  }, [applyFormat, lowLatency]);

  const artist = now?.artist ?? '';
  const title = now?.title ?? '';

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

  const value = useMemo<RadioContextType>(() => ({
    state,
    path,
    now,
    songId,
    loggedId,
    format,
    canPlayOpus,
    canPlayMoq,
    markLogged: setLoggedId,
    setFormat,
    start,
    stop,
    toggle: () => (state === 'off' ? start() : stop())
  }), [state, path, now, songId, loggedId, format, canPlayOpus, canPlayMoq, setFormat, start, stop]);

  return <RadioContext.Provider value={value}>{children}</RadioContext.Provider>;
};

export const useRadio = (): RadioContextType => {
  const context = useContext(RadioContext);

  if (!context) {
    throw new Error('useRadio must be used within a RadioProvider');
  }

  return context;
};
