import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import ListenLog from '../components/ListenLog';
import { usePlayer } from '../contexts/PlayerContext';
import type { RadioStatus } from '../contexts/PlayerContext';
import { formatDateTime } from '../dates';
import { labHere } from '../listen/ways';
import type { Way } from '../listen/ways';
import { pathLabel, trackLabel } from '../radio';
import type { RadioPath } from '../radio';
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
  failed: 'Failed'
};

const card = (on: boolean, state: RadioStatus, path: RadioPath | null, detail: string | null) => {
  if (!on || state === 'off') {
    return { title: 'Ready', text: 'Press play to start the stream.' };
  }

  switch (state) {
    case 'starting':
      return {
        title: 'Tuning In...',
        text: path === 'direct'
          ? 'Establishing connection to the stream. This path needs a few seconds to fill the buffer.'
          : 'Establishing connection to the stream.'
      };
    case 'buffering':
      return { title: 'Live Feed', text: 'Buffering…' };
    case 'retrying':
      return { title: 'Reconnecting...', text: 'The stream was interrupted.' };
    case 'failed':
      return { title: 'Failed', text: detail ?? 'This way did not play.' };
    default:
      return { title: 'Live Feed', text: 'You are listening to the broadcast.' };
  }
};

const RadioPage = () => {
  const {
    mode, way, ways, radioState, radioDetail, path, now, songId, canPlayOpus, canPlayMoq, playRadio, stopRadio
  } = usePlayer();
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

  const supported = (candidate: Way): boolean => {
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
    if (candidate.lab && !lab) {
      return <span className="hint">lab only</span>;
    }

    if (candidate.kind === 'external') {
      return (
        <span className="ways-action">
          <code className="ways-command">{candidate.command}</code>
          <button type="button" onClick={() => copy(candidate)}>{copied === candidate.id ? 'Copied' : 'Copy'}</button>
        </span>
      );
    }

    if (!supported(candidate)) {
      return <span className="hint">not in this browser</span>;
    }

    return (
      <span className="ways-action">
        <button
          type="button"
          aria-pressed={playing(candidate)}
          onClick={() => (playing(candidate) ? stopRadio() : playRadio(candidate.id))}
        >
          {playing(candidate) ? 'Stop' : 'Play'}
        </button>
      </span>
    );
  };

  const statusText = (candidate: Way) => {
    if (!playing(candidate)) {
      return way.id === candidate.id ? 'chosen' : '';
    }

    return radioState === 'failed' && radioDetail ? `${STATE_TEXT.failed} · ${radioDetail}` : STATE_TEXT[radioState];
  };

  const table = (rows: Way[]) => (
    <div className="data-table-wrap">
      <table className="data-table ways-table">
        <thead>
          <tr>
            <th>Way</th>
            <th>Server</th>
            <th>Status</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map(candidate => (
            <tr key={candidate.id} className={way.id === candidate.id ? 'chosen-way' : undefined}>
              <td>
                {candidate.name}
                <div className="post-meta">{candidate.protocol}</div>
              </td>
              <td>{candidate.server}</td>
              <td className="status-line">{statusText(candidate)}</td>
              <td>{action(candidate)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );

  const shown = card(on, radioState, path, radioDetail);

  return (
    <div className="listen-page">
      <h1>Radio</h1>
      <p className="lede">
        One stream for everyone, played by Caligo's radio server: 128 kbit/s MP3, and Opus while its encoder runs, the same
        moment for every listener.
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
          <span className="hint">
            {way.name}
            {on && path !== null && radioState !== 'failed' ? ` · ${pathLabel(path)}` : ''}
          </span>
        </div>
      </div>

      <section className="catalog-section">
        <h2>Every way to listen</h2>
        <p className="hint">
          The same radio through each way the project can deliver it, closest to live first, as the lab measured them. One
          plays at a time, and the one you pick stays chosen for the play buttons above and in the player bar.
        </p>

        <h3>On this site</h3>
        {table(ways.filter(candidate => !candidate.lab))}

        <h3>Lab</h3>
        <p className="hint">
          {lab
            ? 'These come from the lab\'s servers on this machine, fed with the radio by deploy/lab/listen.sh. RTSP, RTMP and SRT don\'t play in browsers: run the command in a terminal with VLC installed.'
            : 'These run only on the development machine: start them with deploy/lab/listen.sh and open this page on localhost.'}
        </p>
        {table(ways.filter(candidate => candidate.lab))}
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
