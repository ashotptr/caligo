import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { PROFILE_REQUIRED, catalogError, statusOf } from '../catalog';
import { deviceId, uuid } from '../playback/device';
import { DELIVERY_LABELS, attachEngine, availableDeliveries } from '../playback/engines';
import type { Delivery, DeliveryChoice, Engine, EngineHooks, Quality } from '../playback/engines';
import { completeSession, endSessionBeforeLeaving, reportPosition, startSession } from '../playback/protocol';
import type { PlaybackSession, SubtitleTrackInfo } from '../playback/protocol';
import { Telemetry } from '../playback/telemetry';

type Phase = 'idle' | 'starting' | 'playing' | 'failed';

type Help = 'signin' | 'profile' | 'subscription' | null;

const COMPLETE_AT = 0.95;
const RECOVERY_GAP_MS = 15000;
const ALL_DELIVERIES: Delivery[] = ['hls', 'hls-native', 'dash', 'progressive'];

const pad = (value: number) => String(value).padStart(2, '0');

const clock = (seconds: number): string => {
  const whole = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);

  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(whole % 60)}` : `${minutes}:${pad(whole % 60)}`;
};

const megabits = (bitrate: number) => `${(bitrate / 1_000_000).toFixed(1)} Mbit/s`;

const VideoPlayer = ({ contentId }: { contentId: number }) => {
  const video = useRef<HTMLVideoElement | null>(null);
  const waitingForVideo = useRef<((element: HTMLVideoElement) => void) | null>(null);
  const unwire = useRef<(() => void) | null>(null);
  const wireRef = useRef<(element: HTMLVideoElement) => () => void>(() => () => undefined);
  const engine = useRef<Engine | null>(null);
  const session = useRef<PlaybackSession | null>(null);
  const telemetry = useRef(new Telemetry());
  const completionId = useRef('');
  const completed = useRef(false);
  const positionSeq = useRef(0);
  const heartbeat = useRef<number | null>(null);
  const intentAt = useRef<number | null>(null);
  const stallAt = useRef<number | null>(null);
  const started = useRef(false);
  const lastRecovery = useRef(0);
  const deliveryRef = useRef<Delivery | null>(null);
  const deliveriesRef = useRef<Delivery[]>([]);
  const choiceRef = useRef<DeliveryChoice>('auto');
  const subtitleRef = useRef<SubtitleTrackInfo | null>(null);
  const recoverRef = useRef<(detail: string) => Promise<void>>(async () => undefined);
  const completeRef = useRef<() => Promise<void>>(async () => undefined);

  const [videoKey, setVideoKey] = useState(0);
  const [phase, setPhase] = useState<Phase>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const [help, setHelp] = useState<Help>(null);
  const [choice, setChoice] = useState<DeliveryChoice>('auto');
  const [delivery, setDelivery] = useState<Delivery | null>(null);
  const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [qualities, setQualities] = useState<Quality[]>([]);
  const [quality, setQuality] = useState(-1);
  const [subtitles, setSubtitles] = useState<SubtitleTrackInfo[]>([]);
  const [subtitle, setSubtitle] = useState(-1);
  const [startupMs, setStartupMs] = useState<number | null>(null);
  const [size, setSize] = useState('');
  const [resumedAt, setResumedAt] = useState<number | null>(null);
  const [finished, setFinished] = useState(false);

  const position = () => video.current?.currentTime ?? 0;

  const setVideoElement = useCallback((element: HTMLVideoElement | null) => {
    if (element === video.current) {
      return;
    }

    unwire.current?.();
    unwire.current = null;
    video.current = element;

    if (element) {
      unwire.current = wireRef.current(element);
      waitingForVideo.current?.(element);
      waitingForVideo.current = null;
    }
  }, []);

  const freshVideo = () => new Promise<HTMLVideoElement>(resolve => {
    waitingForVideo.current = resolve;
    setVideoKey(key => key + 1);
  });

  const stopHeartbeat = () => {
    if (heartbeat.current !== null) {
      window.clearInterval(heartbeat.current);
      heartbeat.current = null;
    }
  };

  const detach = () => {
    engine.current?.destroy();
    engine.current = null;
  };

  const fail = (text: string, why: Help = null) => {
    stopHeartbeat();
    detach();
    setMessage(text);
    setHelp(why);
    setPhase('failed');
  };

  const explain = (err: unknown) => {
    const status = statusOf(err);
    const text = catalogError(err, 'Playback could not start.');

    if (status === 401) {
      fail('Sign in to watch.', 'signin');
    }
    else if (status === 409 && text === PROFILE_REQUIRED) {
      fail(text, 'profile');
    }
    else if (status === 403) {
      fail(text, 'subscription');
    }
    else if (status === 404) {
      fail('This title has no video yet.');
    }
    else {
      fail(text);
    }
  };

  const beat = async () => {
    const current = session.current;

    if (!current) {
      return;
    }

    positionSeq.current += 1;

    try {
      await reportPosition(current.session_id, position(), positionSeq.current);
    } catch (err) {
      const status = statusOf(err);

      if (status === 410 || status === 404) {
        await recoverRef.current('the playback session ended');
      }
    }
  };

  const adopt = (opened: PlaybackSession) => {
    stopHeartbeat();
    session.current = opened;
    telemetry.current.bind(opened.session_id);
    completionId.current = uuid();
    completed.current = false;
    positionSeq.current = 0;
    setSubtitles(opened.subtitles);
    heartbeat.current = window.setInterval(() => void beat(), Math.max(opened.heartbeat_seconds, 5) * 1000);
  };

  const hooks: EngineHooks = {
    onQualities: list => setQualities(list),
    onQualitySwitch: (from, to) => telemetry.current.record('quality', {
      from, to, position: position(), delivery: deliveryRef.current ?? undefined
    }),
    onFatal: (status, detail) => {
      telemetry.current.record('error', {
        detail: status ? `${detail} (HTTP ${status})` : detail, position: position(), delivery: deliveryRef.current ?? undefined
      });
      void recoverRef.current(detail);
    }
  };

  const attach = async (next: Delivery, startAt: number) => {
    const current = session.current;

    if (!current) {
      return;
    }

    detach();
    started.current = false;
    stallAt.current = null;
    deliveryRef.current = next;
    setDelivery(next);
    setQualities([]);
    setQuality(-1);
    setSize('');

    const element = await freshVideo();
    const attached = await attachEngine(next, element, current, startAt, hooks);

    engine.current = attached;

    if (subtitleRef.current) {
      attached.setSubtitle(subtitleRef.current);
    }

    try {
      await element.play();
    } catch {
      setMessage('Press play to start.');
    }
  };

  const recover = async (detail: string) => {
    const kind = deliveryRef.current;

    if (!kind || !session.current) {
      return;
    }

    const now = Date.now();
    const at = position();

    if (now - lastRecovery.current < RECOVERY_GAP_MS) {
      const list = deliveriesRef.current;
      const next = choiceRef.current === 'auto' ? list[list.indexOf(kind) + 1] : undefined;

      lastRecovery.current = now;

      if (!next) {
        fail(`Playback stopped: ${detail}.`);

        return;
      }

      await attach(next, at);

      return;
    }

    lastRecovery.current = now;

    try {
      adopt(await startSession(contentId, deviceId()));
      await attach(kind, at);
    } catch (err) {
      explain(err);
    }
  };

  recoverRef.current = recover;

  const begin = async () => {
    const element = video.current;

    if (!element) {
      return;
    }

    intentAt.current = performance.now();
    setPhase('starting');
    setMessage(null);
    setHelp(null);
    setFinished(false);

    try {
      const opened = await startSession(contentId, deviceId());

      adopt(opened);

      const list = availableDeliveries(opened, element);
      const wanted = choiceRef.current;
      const next = wanted !== 'auto' && list.includes(wanted) ? wanted : list[0];

      deliveriesRef.current = list;
      setDeliveries(list);

      if (!next) {
        fail('This browser cannot play any of the formats this title comes in.');

        return;
      }

      setResumedAt(opened.position > 1 ? opened.position : null);
      setPhase('playing');
      await attach(next, opened.position);
    } catch (err) {
      explain(err);
    }
  };

  completeRef.current = async () => {
    const current = session.current;

    if (!current || completed.current) {
      return;
    }

    completed.current = true;

    try {
      await completeSession(current.session_id, completionId.current, position());
      setFinished(true);
    } catch (err) {
      completed.current = statusOf(err) === 409;
    }
  };

  wireRef.current = element => {
    const track = telemetry.current;
    const where = () => ({ position: element.currentTime, delivery: deliveryRef.current ?? undefined });

    const onPlaying = () => {
      if (!started.current) {
        started.current = true;

        if (intentAt.current !== null) {
          const took = performance.now() - intentAt.current;

          intentAt.current = null;
          setStartupMs(took);
          track.record('startup', { ...where(), duration_ms: Math.round(took) });
        }

        return;
      }

      if (stallAt.current !== null) {
        track.record('stall', { ...where(), duration_ms: Math.round(performance.now() - stallAt.current) });
        stallAt.current = null;
      }
    };

    const onWaiting = () => {
      if (started.current && !element.seeking && stallAt.current === null) {
        stallAt.current = performance.now();
      }
    };

    const onSeeking = () => {
      stallAt.current = null;

      if (started.current) {
        track.record('seek', where());
      }
    };

    const onPause = () => {
      if (started.current && !element.ended) {
        track.record('pause', where());
      }
    };

    const onPlay = () => {
      if (started.current) {
        track.record('play', where());
      }
    };

    const onTime = () => {
      const duration = Number.isFinite(element.duration) && element.duration > 0
        ? element.duration
        : session.current?.duration_seconds ?? 0;

      if (duration > 0 && element.currentTime >= duration * COMPLETE_AT) {
        void completeRef.current();
      }
    };

    const onEnded = () => {
      track.record('ended', where());
      void completeRef.current();
    };

    const onSize = () => {
      setSize(element.videoWidth > 0 ? `${element.videoWidth}×${element.videoHeight}` : '');
    };

    const events: [string, () => void][] = [
      ['playing', onPlaying],
      ['waiting', onWaiting],
      ['seeking', onSeeking],
      ['pause', onPause],
      ['play', onPlay],
      ['timeupdate', onTime],
      ['ended', onEnded],
      ['resize', onSize],
      ['loadedmetadata', onSize]
    ];

    for (const [name, handler] of events) {
      element.addEventListener(name, handler);
    }

    return () => {
      for (const [name, handler] of events) {
        element.removeEventListener(name, handler);
      }
    };
  };

  useEffect(() => {
    const track = telemetry.current;

    const leave = () => {
      if (heartbeat.current !== null) {
        window.clearInterval(heartbeat.current);
        heartbeat.current = null;
      }

      track.flushBeforeLeaving();

      if (session.current) {
        endSessionBeforeLeaving(session.current.session_id);
        session.current = null;
      }

      engine.current?.destroy();
      engine.current = null;
    };

    window.addEventListener('pagehide', leave);

    return () => {
      window.removeEventListener('pagehide', leave);
      leave();
    };
  }, []);

  const chooseDelivery = (value: DeliveryChoice) => {
    setChoice(value);
    choiceRef.current = value;

    if (phase === 'playing' && session.current) {
      const list = deliveriesRef.current;
      const next = value === 'auto' ? list[0] : value;

      if (next && list.includes(next) && next !== deliveryRef.current) {
        intentAt.current = performance.now();
        void attach(next, position());
      }
    }
  };

  const chooseQuality = (index: number) => {
    setQuality(index);
    engine.current?.setQuality(index);
  };

  const chooseSubtitle = (index: number) => {
    const track = index >= 0 ? subtitles[index] ?? null : null;

    setSubtitle(index);
    subtitleRef.current = track;
    engine.current?.setSubtitle(track);
  };

  const offered = deliveries.length > 0 ? deliveries : ALL_DELIVERIES;

  return (
    <div className="video-player">
      <div className="video-frame">
        <video key={videoKey} ref={setVideoElement} className="video-element" controls playsInline preload="none" />

        {phase !== 'playing' && (
          <div className="video-overlay">
            {phase === 'idle' && (
              <button type="button" className="video-start" onClick={() => void begin()}>Play</button>
            )}

            {phase === 'starting' && <span className="video-wait">Starting…</span>}

            {phase === 'failed' && (
              <div className="video-error">
                <p>{message}</p>
                {help === 'signin' && <Link to="/login">Sign in</Link>}
                {help === 'profile' && <Link to="/streaming">Create your streaming profile</Link>}
                {help === 'subscription' && <Link to="/streaming">Renew your subscription</Link>}
                <button type="button" onClick={() => void begin()}>Try again</button>
              </div>
            )}
          </div>
        )}
      </div>

      <div className="video-choices">
        <label>
          Delivery
          <select value={choice} onChange={event => chooseDelivery(event.target.value as DeliveryChoice)}>
            <option value="auto">Automatic</option>
            {offered.map(item => <option key={item} value={item}>{DELIVERY_LABELS[item]}</option>)}
          </select>
        </label>

        {qualities.length > 0 && (
          <label>
            Quality
            <select value={quality} onChange={event => chooseQuality(Number(event.target.value))}>
              <option value={-1}>Automatic</option>
              {qualities.map(item => (
                <option key={item.index} value={item.index}>{item.height}p · {megabits(item.bitrate)}</option>
              ))}
            </select>
          </label>
        )}

        {subtitles.length > 0 && (
          <label>
            Subtitles
            <select value={subtitle} onChange={event => chooseSubtitle(Number(event.target.value))}>
              <option value={-1}>Off</option>
              {subtitles.map((item, index) => <option key={item.url} value={index}>{item.label}</option>)}
            </select>
          </label>
        )}
      </div>

      {phase === 'playing' && delivery && (
        <p className="hint video-facts">
          {DELIVERY_LABELS[delivery]}
          {size && ` · ${size}`}
          {startupMs !== null && ` · first frame after ${(startupMs / 1000).toFixed(2)} s`}
          {resumedAt !== null && ` · resumed at ${clock(resumedAt)}`}
          {finished && ' · added to your history'}
          {message && ` · ${message}`}
        </p>
      )}
    </div>
  );
};

export default VideoPlayer;
