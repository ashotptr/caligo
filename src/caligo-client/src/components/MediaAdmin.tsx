import { useEffect, useState } from 'react';
import { useGetMediaInboxQuery, useGetMediaQuery, useQueueMediaMutation } from '../api/catalogApi';
import { catalogError, statusOf } from '../catalog';
import type { MediaInfo } from '../types';

const POLL_MS = 3000;

const sizeOf = (bytes: number): string =>
  bytes >= 1_073_741_824 ? `${(bytes / 1_073_741_824).toFixed(1)} GB` : `${(bytes / 1_048_576).toFixed(1)} MB`;

const busy = (media: MediaInfo | null | undefined) => media?.status === 'queued' || media?.status === 'packaging';

const MediaAdmin = ({ contentId }: { contentId: number }) => {
  const [polling, setPolling] = useState(false);
  const { data, error: mediaError, isLoading } = useGetMediaQuery(contentId, { pollingInterval: polling ? POLL_MS : 0 });
  const { data: inbox, error: inboxError } = useGetMediaInboxQuery();
  const [queueMedia, { isLoading: sending }] = useQueueMediaMutation();
  const [source, setSource] = useState('');
  const [note, setNote] = useState<string | null>(null);

  const media: MediaInfo | null | undefined = isLoading
    ? undefined
    : statusOf(mediaError) === 404 ? null : data ?? null;

  useEffect(() => {
    setPolling(busy(media));
  }, [media]);

  useEffect(() => {
    if (inbox && inbox.length > 0) {
      setSource(current => current || inbox[0].file);
    }
  }, [inbox]);

  const failure = note
    ?? (mediaError && statusOf(mediaError) !== 404 ? catalogError(mediaError, 'Could not read the media status.') : null)
    ?? (inboxError ? catalogError(inboxError, 'Could not list the incoming folder.') : null);

  const queue = async () => {
    setNote(null);

    try {
      await queueMedia({ contentId, sourceFile: source }).unwrap();
    } catch (err) {
      setNote(catalogError(err, 'Could not queue the video.'));
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

      {inbox !== undefined && inbox.length === 0 && <p className="hint">The incoming folder has no video files.</p>}

      {inbox !== undefined && inbox.length > 0 && (
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

      {failure && <p className="form-error">{failure}</p>}
    </section>
  );
};

export default MediaAdmin;
