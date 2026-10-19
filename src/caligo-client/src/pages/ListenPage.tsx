import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import ListenLog from '../components/ListenLog';
import { useRadio } from '../contexts/RadioContext';
import { formatDateTime } from '../dates';
import { pathLabel, trackLabel } from '../radio';
import type { RadioHistory, RadioPlay } from '../types';

const REFRESH_MS = 60000;
const LISTENERS_MS = 15000;

const ListenPage = () => {
  const { state, path, now, songId, toggle, format, setFormat, canPlayOpus, canPlayMoq } = useRadio();
  const [plays, setPlays] = useState<RadioPlay[] | null>(null);
  const [listeners, setListeners] = useState<number | null>(null);
  const [opusOnAir, setOpusOnAir] = useState(false);

  const on = state !== 'off';
  const current = now ? `${now.artist}|${now.title}` : '';

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
        .then((data: { listeners?: number; opus?: boolean } | null) => {
          if (!cancelled) {
            setListeners(typeof data?.listeners === 'number' ? data.listeners : null);
            setOpusOnAir(data?.opus === true);
          }
        })
        .catch(() => {
          if (!cancelled) {
            setListeners(null);
            setOpusOnAir(false);
          }
        });
    };

    load();

    const timer = window.setInterval(load, LISTENERS_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [state]);

  return (
    <div className="listen-page">
      <h1>Radio</h1>
      <p className="lede">
        One stream for everyone, played by Caligo's radio server: 128 kbit/s MP3, and Opus while its encoder runs, the same
        moment for every listener.
        {listeners !== null && <> {listeners} listening now.</>}
      </p>

      {(canPlayOpus || canPlayMoq) && (opusOnAir || format !== 'mp3') && (
        <div className="tabs" role="group" aria-label="Stream format">
          <button type="button" aria-pressed={format === 'mp3'} onClick={() => setFormat('mp3')}>MP3</button>
          {canPlayOpus && (
            <button type="button" aria-pressed={format === 'opus'} onClick={() => setFormat('opus')}>Opus</button>
          )}
          {canPlayMoq && (
            <button type="button" aria-pressed={format === 'moq'} onClick={() => setFormat('moq')}>Low latency</button>
          )}
        </div>
      )}

      <div className="listen-control">
        <button type="button" onClick={toggle} aria-pressed={on}>
          {on ? 'Stop' : 'Play'}
        </button>

        <div>
          {state === 'off' && <p className="status-line">Press play to tune in.</p>}
          {state === 'starting' && (
            <p className="status-line">
              {path === 'direct' ? 'Tuning in directly… this path needs a few seconds to fill the buffer.' : 'Tuning in…'}
            </p>
          )}
          {state === 'buffering' && <p className="status-line">Buffering…</p>}
          {state === 'retrying' && <p className="status-line">The stream stalled, reconnecting…</p>}

          {on && now && (
            <>
              <p className="listen-now">
                {songId !== null ? <Link to={`/catalog/${songId}`}>{trackLabel(now)}</Link> : trackLabel(now)}
                {now.isRequest && <span className="badge">request</span>}
              </p>
              {now.album && now.album !== 'Unknown' && <p className="post-meta">{now.album}</p>}
              {songId !== null && <p className="post-meta"><ListenLog contentId={songId} /></p>}
              <p className="hint">{pathLabel(path)}</p>
            </>
          )}
        </div>
      </div>

      {plays !== null && (
        <section className="catalog-section">
          <h2>Recently played</h2>
          {plays.length === 0 && <p className="status-line">Nothing recorded yet.</p>}
          <ul className="dashboard-list">
            {plays.map(play => (
              <li key={play.startedAt}>
                <span>
                  {play.contentId !== null
                    ? <Link to={`/catalog/${play.contentId}`}>{play.title}</Link>
                    : play.title}
                  <span className="post-meta"> · {play.artist}{play.isRequest ? ' · request' : ''}</span>
                </span>
                <span className="post-meta">{formatDateTime(play.startedAt)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="catalog-section">
        <h2>Caligo</h2>
        <p className="lede">
          The radio comes from Caligo's engine, which also has a chat, album collections, song requests and an assistant.
          Its original page opens below.
        </p>
        <p><a className="button-link" href="/caligo">Open Caligo</a></p>
      </section>
    </div>
  );
};

export default ListenPage;
