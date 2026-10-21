import { useRef, useState } from 'react';
import { streamFailure, streamLines, toneOf } from '../caligoStream';
import type { StreamTone } from '../caligoStream';

interface Status {
  text: string;
  tone: StreamTone;
}

const DownloadIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
    <polyline points="7 10 12 15 17 10" />
    <line x1="12" y1="15" x2="12" y2="3" />
  </svg>
);

const LoadingIcon = () => (
  <svg className="spinner" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <line x1="12" y1="2" x2="12" y2="6" />
    <line x1="12" y1="18" x2="12" y2="22" />
    <line x1="4.93" y1="4.93" x2="7.76" y2="7.76" />
    <line x1="16.24" y1="16.24" x2="19.07" y2="19.07" />
    <line x1="2" y1="12" x2="6" y2="12" />
    <line x1="18" y1="12" x2="22" y2="12" />
    <line x1="4.93" y1="19.07" x2="7.76" y2="16.24" />
    <line x1="16.24" y1="7.76" x2="19.07" y2="4.93" />
  </svg>
);

const IngestPanel = () => {
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [used, setUsed] = useState(false);
  const [status, setStatus] = useState<Status | null>(null);
  const input = useRef<HTMLInputElement>(null);

  const ingest = async () => {
    const cleaned = query.trim();

    if (!cleaned || busy) {
      return;
    }

    setQuery('');
    setBusy(true);
    setStatus({ text: '> INITIALIZING UPLINK...', tone: 'muted' });

    let tone: StreamTone = 'muted';

    try {
      const ended = await streamLines('/api/caligo/ingest', { query: cleaned }, line => {
        tone = toneOf(line, tone);
        setStatus({ text: `> ${line}`, tone });
      });

      setStatus(ended ? { text: '> DONE. REFRESH TO VIEW.', tone: 'done' } : { text: '> CONNECTION ERROR', tone: 'error' });
    } catch (err) {
      setStatus({ text: `> ${streamFailure(err)}`, tone: 'error' });
    } finally {
      setBusy(false);
      setUsed(true);
      window.setTimeout(() => input.current?.focus(), 0);
    }
  };

  return (
    <div className="acquisition-panel">
      <div className="input-container">
        <input
          ref={input}
          type="text"
          className="caligo-input"
          placeholder={busy ? 'Processing...' : used ? 'Use Link...' : 'Use Spotify Link or Search...'}
          autoComplete="off"
          maxLength={500}
          disabled={busy}
          value={query}
          aria-label="Spotify link or search"
          onChange={event => setQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'Enter') ingest();
          }}
        />
        <button type="button" className="acquire-btn" title="Ingest" aria-label="Ingest" onClick={ingest} disabled={busy}>
          {busy ? <LoadingIcon /> : <DownloadIcon />}
        </button>
      </div>
      {status && <div className={`status-line ingest-status ${status.tone}`}>{status.text}</div>}
    </div>
  );
};

export default IngestPanel;
