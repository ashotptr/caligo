import { Link } from 'react-router-dom';
import { formatDuration, kindLabel } from '../catalog';
import { useAuth } from '../contexts/AuthContext';
import { useCollections } from '../contexts/CollectionsContext';
import { usePlayer } from '../contexts/PlayerContext';
import type { PlayerTrack } from '../contexts/PlayerContext';
import { useToast } from '../contexts/ToastContext';
import { downloadAll, downloadTrack } from '../downloads';
import { titlePath } from '../thesaurarium';
import type { ContentKind, Track } from '../types';
import RadioQueueButton from './RadioQueueButton';

export type TrackRow = Track & { kind?: ContentKind; coverUri?: string | null };

interface TrackListProps {
  tracks: TrackRow[];
  numbered?: boolean;
  album?: string;
  albums?: Record<number, string>;
  cover?: string | null;
  download?: string;
  inCollection?: boolean;
  onRemove?: (track: TrackRow) => void;
}

export const DownloadIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
    <polyline points="7 10 12 15 17 10" />
    <line x1="12" y1="15" x2="12" y2="3" />
  </svg>
);

const TrackList = ({ tracks, numbered = true, album, albums, cover, download, inCollection = false, onRemove }: TrackListProps) => {
  const { isAuthenticated } = useAuth();
  const { addToCollection } = useCollections();
  const { mode, current, playTracks } = usePlayer();
  const toast = useToast();

  const playable = tracks.filter((track): track is TrackRow & { audioUri: string } => !!track.audioUri);

  const queue: PlayerTrack[] = playable.map(track => ({
    contentId: track.contentId,
    title: track.title,
    artist: track.artists[0] ?? '',
    album: albums?.[track.contentId] ?? album ?? '',
    audioUri: track.audioUri,
    coverUri: track.coverUri ?? cover ?? null
  }));

  const play = (track: TrackRow) => {
    const at = playable.findIndex(candidate => candidate.contentId === track.contentId);

    if (at >= 0) {
      playTracks(queue, at);
    }
  };

  const clicked = (event: React.MouseEvent, track: TrackRow) => {
    if (!(event.target as HTMLElement).closest('button, a')) {
      play(track);
    }
  };

  if (tracks.length === 0) {
    return <p className="status-line">{inCollection ? 'No tracks in this collection.' : 'No songs here yet.'}</p>;
  }

  return (
    <div>
      {download !== undefined && playable.length > 0 && (
        <div className="album-actions">
          <button type="button" onClick={() => playTracks(queue, 0)}>▶ Play All</button>
          <button type="button" className="btn-secondary" onClick={() => downloadAll(download, playable, toast)}>
            ↓ Download All
          </button>
        </div>
      )}

      <ol className={numbered ? 'track-list' : 'track-list unnumbered'}>
        {tracks.map((track, index) => {
          const active = mode === 'track' && current?.contentId === track.contentId;
          const kind = track.kind ?? 'song';

          return (
            <li
              key={track.contentId}
              className={`${active ? 'playing' : ''}${track.audioUri ? ' playable' : ''}`}
              onClick={event => clicked(event, track)}
            >
              {numbered && <span className="track-number">{track.trackNumber ?? index + 1}</span>}
              <span className="track-title">
                <Link to={titlePath(track.contentId)}>{track.title}</Link>
                {kind !== 'song' && <span className="post-meta"> · {kindLabel(kind)}</span>}
                {track.artists.length > 0 && <span className="post-meta"> · {track.artists.join(', ')}</span>}
              </span>
              <span className="track-actions">
                {isAuthenticated && !inCollection && (
                  <button
                    type="button"
                    className="btn-secondary track-add"
                    title="Add to Collection"
                    onClick={() => addToCollection([track.contentId], track.title)}
                  >
                    ＋
                  </button>
                )}
                <RadioQueueButton contentId={track.contentId} audioUri={track.audioUri} />
                {track.audioUri && (
                  <button
                    type="button"
                    className="download-btn"
                    title="Download Track"
                    onClick={() => downloadTrack(track.audioUri!, track.title)}
                  >
                    <DownloadIcon />
                  </button>
                )}
                {!track.audioUri && kind === 'song' && <span className="hint">no file</span>}
                {onRemove && (
                  <button
                    type="button"
                    className="link-button danger"
                    title="Remove from the collection"
                    onClick={() => onRemove(track)}
                  >
                    ✕
                  </button>
                )}
              </span>
              <span className="track-duration">{formatDuration(track.duration, kind)}</span>
            </li>
          );
        })}
      </ol>
    </div>
  );
};

export default TrackList;
