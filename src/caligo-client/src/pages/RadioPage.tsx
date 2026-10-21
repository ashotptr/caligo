import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import ListenLog from '../components/ListenLog';
import { usePlayer } from '../contexts/PlayerContext';
import type { RadioStatus } from '../contexts/PlayerContext';
import { formatDateTime } from '../dates';
import { labHere } from '../listen/ways';
import type { Way } from '../listen/ways';
import { trackLabel } from '../radio';
import { titlePath } from '../thesaurarium';
import type { RadioHistory, RadioPlay } from '../types';

const REFRESH_MS = 60000;
const LISTENERS_MS = 15000;

const STATE_TEXT: Record<RadioStatus, string> = {
  off: '',
  starting: 'Connecting…',
  playing: 'Playing',
  buffering: 'Buffering…',
  retrying: 'Reconnecting…',
  failed: 'Couldn\'t play'
};

const card = (on: boolean, state: RadioStatus) => {
  if (!on || state === 'off') {
    return { title: 'Ready', text: 'Press play to start the stream.' };
  }

  switch (state) {
    case 'starting':
      return { title: 'Tuning In...', text: 'Establishing connection to the stream.' };
    case 'buffering':
      return { title: 'Live Feed', text: 'Buffering…' };
    case 'retrying':
      return { title: 'Reconnecting...', text: 'The stream was interrupted.' };
    case 'failed':
      return { title: 'Couldn\'t play', text: 'Try another way to listen below.' };
    default:
      return { title: 'Live Feed', text: 'You are listening to the broadcast.' };
  }
};

const RadioPage = () => {
  const { mode, way, ways, radioState, now, songId, canPlayOpus, canPlayMoq, playRadio, stopRadio } = usePlayer();
  const lab = labHere();
  const [plays, setPlays] = useState<RadioPlay[] | null>(null);
  const [listeners, setListeners] = useState<number | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const started = useRef(false);

  const on = mode === 'radio';
  const current = now ? `${now.artist}|${now.title}` : '';

  useEffect(() => {
    if (!started.current && mode === 'off') {
      playRadio();
    }

    started.current = true;
  }, [mode, playRadio]);

  useEffect(() => {
    let cancelled = false;

    const load = () => {
      api
        .get<RadioHistory>('/api/radio/recent', { params: { limit: 20 } })
        .then(response => {
          if (!cancelled) setPlays(response.data.enabled ? response.data.plays : null);
        })
        .catch(() => undefined);
    };

    load();

    const timer = window.setInterval(load, REFRESH_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [current]);

  useEffect(() => {
    let cancelled = false;

    const load = () => {
      fetch('/live/now')
        .then(response => (response.ok ? response.json() : null))
        .then((data: { listeners?: number } | null) => {
          if (!cancelled) setListeners(typeof data?.listeners === 'number' ? data.listeners : null);
        })
        .catch(() => {
          if (!cancelled) setListeners(null);
        });
    };

    load();

    const timer = window.setInterval(load, LISTENERS_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [radioState]);

  const playable = (candidate: Way): boolean => {
    if ((candidate.lab && !lab) || candidate.kind === 'external') return false;
    if (candidate.kind === 'stream' && candidate.format === 'opus') return canPlayOpus;
    if (candidate.kind === 'objects' || candidate.kind === 'moq-lite') {
      return canPlayMoq && (!candidate.webTransport || 'WebTransport' in window);
    }

    return true;
  };

  const copy = (candidate: Way) => {
    if (!candidate.command) {
      return;
    }

    navigator.clipboard
      .writeText(candidate.command)
      .then(() => setCopied(candidate.id))
      .catch(() => setCopied(null));
  };

  const playing = (candidate: Way) => on && way.id === candidate.id;

  const action = (candidate: Way) => {
    if (candidate.kind === 'external' && lab && candidate.command) {
      return (
        <button type="button" className="btn-secondary" title={candidate.command} onClick={() => copy(candidate)}>
          {copied === candidate.id ? 'Copied' : 'Copy for VLC'}
        </button>
      );
    }

    if (!playable(candidate)) {
      return null;
    }

    return (
      <button
        type="button"
        aria-pressed={playing(candidate)}
        onClick={() => (playing(candidate) ? stopRadio() : playRadio(candidate.id))}
      >
        {playing(candidate) ? 'Stop' : 'Play'}
      </button>
    );
  };

  const shown = card(on, radioState);

  return (
    <div className="listen-page">
      <h1>Radio</h1>
      <p className="lede">
        One stream for everyone, the same moment for every listener.
        {listeners !== null && <> {listeners} listening now.</>}
      </p>

      <div className="radio-card">
        <h2>{shown.title}</h2>
        <p className="radio-card-text">{shown.text}</p>

        {on && now && (
          <div className="radio-card-now">
            <p className="listen-now">
              {songId !== null ? <Link to={titlePath(songId)}>{trackLabel(now)}</Link> : trackLabel(now)}
              {now.isRequest && <span className="badge">request</span>}
            </p>
            {now.album && now.album !== 'Unknown' && <p className="post-meta">{now.album}</p>}
            {songId !== null && <p className="post-meta"><ListenLog contentId={songId} /></p>}
          </div>
        )}

        <div className="radio-card-actions">
          <button type="button" onClick={() => (on ? stopRadio() : playRadio())} aria-pressed={on}>
            {on ? 'Stop' : 'Play'}
          </button>
          <span className="hint">{way.name}</span>
        </div>
      </div>

      <section className="catalog-section">
        <h2>Ways to listen</h2>
        <ul className="ways-list">
          {ways.map(candidate => (
            <li
              key={candidate.id}
              className={`way${way.id === candidate.id ? ' chosen' : ''}${playable(candidate) ? '' : ' unavailable'}`}
            >
              <span className="way-name">
                {candidate.name}
                <span className="way-note">{candidate.note}</span>
              </span>
              <span className="way-status">{playing(candidate) ? STATE_TEXT[radioState] : ''}</span>
              {action(candidate)}
            </li>
          ))}
        </ul>
      </section>

      {plays !== null && (
        <section className="catalog-section">
          <h2>Recently played</h2>
          {plays.length === 0 && <p className="status-line">Nothing recorded yet.</p>}
          <ul className="dashboard-list">
            {plays.map(play => (
              <li key={play.startedAt}>
                <span>
                  {play.contentId !== null
                    ? <Link to={titlePath(play.contentId)}>{play.title}</Link>
                    : play.title}
                  <span className="post-meta"> · {play.artist}{play.isRequest ? ' · request' : ''}</span>
                </span>
                <span className="post-meta">{formatDateTime(play.startedAt)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
};

export default RadioPage;
