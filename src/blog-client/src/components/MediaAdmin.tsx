import { useCallback, useEffect, useState } from 'react';
import api from '../api/axios';
import { catalogError, statusOf } from '../catalog';
import type { InboxFile, MediaInfo } from '../types';

const POLL_MS = 3000;

const sizeOf = (bytes: number): string =>
  bytes >= 1_073_741_824 ? `${(bytes / 1_073_741_824).toFixed(1)} GB` : `${(bytes / 1_048_576).toFixed(1)} MB`;

const busy = (media: MediaInfo | null | undefined) => media?.status === 'queued' || media?.status === 'packaging';

const MediaAdmin = ({ contentId, onChanged }: { contentId: number; onChanged: (media: MediaInfo | null) => void }) => {
  const [media, setMedia] = useState<MediaInfo | null | undefined>(undefined);
  const [inbox, setInbox] = useState<InboxFile[] | null>(null);
  const [source, setSource] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const [sending, setSending] = useState(false);

  const loadMedia = useCallback(async () => {
    try {
      const response = await api.get<MediaInfo>(`/api/content/${contentId}/media`);

      setMedia(response.data);
      onChanged(response.data);
    } catch (err) {
      if (statusOf(err) === 404) {
        setMedia(null);
        onChanged(null);
      } else {
        setNote(catalogError(err, 'Could not read the media status.'));
      }
    }
  }, [contentId, onChanged]);

  useEffect(() => {
    void loadMedia();

    api.get<InboxFile[]>('/api/media/inbox')
      .then(response => {
        setInbox(response.data);
        setSource(current => current || response.data[0]?.file || '');
      })
      .catch(err => setNote(catalogError(err, 'Could not list the incoming folder.')));
  }, [loadMedia]);

  useEffect(() => {
    if (!busy(media)) {
      return;
    }

    const timer = window.setInterval(() => void loadMedia(), POLL_MS);

    return () => window.clearInterval(timer);
  }, [media, loadMedia]);

  const queue = async () => {
    setSending(true);
    setNote(null);

    try {
      const response = await api.post<MediaInfo>(`/api/content/${contentId}/media`, { sourceFile: source });

      setMedia(response.data);
    } catch (err) {
      setNote(catalogError(err, 'Could not queue the video.'));
    } finally {
      setSending(false);
    }
  };

  const chosen = inbox?.find(file => file.file === source);

  return (
    <section className="catalog-section media-admin">
      <h2>Video (admin)</h2>

      {media === undefined && <p className="status-line">Loading…</p>}
      {media === null && <p className="hint">No video yet.</p>}

      {media && (
        <p className="hint">
          {media.status === 'ready' && `Ready: ${media.formats.join(', ')} · ${media.renditions.length} renditions`}
          {media.status === 'queued' && 'Waiting for the packager…'}
          {media.status === 'packaging' && 'The packager is working on it…'}
          {media.status === 'failed' && `Packaging failed: ${media.error ?? 'no reason given'}`}
          {media.sourceFile && ` · source ${media.sourceFile}`}
          {media.playable && media.status !== 'ready' && ' · the previous package still plays'}
        </p>
      )}

      {inbox !== null && inbox.length === 0 && <p className="hint">The incoming folder has no video files.</p>}

      {inbox !== null && inbox.length > 0 && (
        <div className="media-admin-form">
          <select value={source} onChange={event => setSource(event.target.value)} disabled={busy(media)}>
            {inbox.map(file => (
              <option key={file.file} value={file.file}>{file.file} · {sizeOf(file.size)}</option>
            ))}
          </select>
          <button type="button" onClick={() => void queue()} disabled={!source || sending || busy(media)}>
            {media?.playable ? 'Package again' : 'Package'}
          </button>
        </div>
      )}

      {chosen && chosen.subtitles.length > 0 && (
        <p className="hint">
          Subtitles next to it:{' '}
          {chosen.subtitles.map(subtitle => `${subtitle.language}${subtitle.known ? '' : ' (unknown language, skipped)'}`).join(', ')}
        </p>
      )}

      {note && <p className="form-error">{note}</p>}
    </section>
  );
};

export default MediaAdmin;
