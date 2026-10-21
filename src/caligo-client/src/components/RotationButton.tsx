import { useState } from 'react';
import { streamFailure, streamLines, toneOf } from '../caligoStream';
import type { StreamTone } from '../caligoStream';

interface RotationButtonProps {
  albumId?: number;
  singleId?: number;
  contentId?: number;
}

const RotationButton = ({ albumId, singleId, contentId }: RotationButtonProps) => {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<{ text: string; tone: StreamTone } | null>(null);

  const add = async () => {
    setBusy(true);
    setStatus({ text: '> INITIALIZING UPLINK...', tone: 'muted' });

    let tone: StreamTone = 'muted';
    let last = '';

    try {
      const ended = await streamLines('/api/caligo/rotation', { albumId, singleId, contentId }, line => {
        last = line;
        tone = toneOf(line, tone);
        setStatus({ text: `> ${line}`, tone });
      });

      if (!ended) {
        setStatus({ text: '> CONNECTION ERROR', tone: 'error' });
      } else if (!last) {
        setStatus({ text: '> DONE.', tone: 'done' });
      }
    } catch (err) {
      setStatus({ text: `> ${streamFailure(err)}`, tone: 'error' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rotation-control">
      <button type="button" className="btn-secondary" onClick={add} disabled={busy}>
        {busy ? 'Adding to the radio rotation…' : '＋ Add to radio rotation'}
      </button>
      {status && <span className={`status-line ingest-status ${status.tone}`}>{status.text}</span>}
    </div>
  );
};

export default RotationButton;
