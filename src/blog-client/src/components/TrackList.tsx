import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { formatDuration } from '../catalog';
import type { Track } from '../types';

const TrackList = ({ tracks, numbered = true }: { tracks: Track[]; numbered?: boolean }) => {
  const [playing, setPlaying] = useState<number | null>(null);
  const audio = useRef<HTMLAudioElement>(null);

  const current = tracks.find(track => track.contentId === playing) ?? null;

  const play = (track: Track) => {
    if (playing === track.contentId) {
      audio.current?.pause();
      setPlaying(null);
    } else {
      setPlaying(track.contentId);
    }
  };

  const next = () => {
    const index = tracks.findIndex(track => track.contentId === playing);
    const following = tracks.slice(index + 1).find(track => track.audioUri);

    setPlaying(following ? following.contentId : null);
  };

  if (tracks.length === 0) {
    return <p className="status-line">No songs here yet.</p>;
  }

  return (
    <div>
      <ol className={numbered ? 'track-list' : 'track-list unnumbered'}>
        {tracks.map(track => (
          <li key={track.contentId} className={playing === track.contentId ? 'playing' : undefined}>
            {numbered && <span className="track-number">{track.trackNumber ?? ''}</span>}
            <span className="track-title">
              <Link to={`/catalog/${track.contentId}`}>{track.title}</Link>
              {track.artists.length > 0 && <span className="post-meta"> · {track.artists.join(', ')}</span>}
            </span>
            <span className="track-duration">{formatDuration(track.duration, 'song')}</span>
            {track.audioUri ? (
              <button type="button" className="link-button" onClick={() => play(track)}>
                {playing === track.contentId ? 'Stop' : 'Play'}
              </button>
            ) : (
              <span className="hint">no file</span>
            )}
          </li>
        ))}
      </ol>

      {current?.audioUri && (
        <audio
          ref={audio}
          key={current.contentId}
          className="track-player"
          src={current.audioUri}
          controls
          autoPlay
          preload="none"
          onEnded={next}
        />
      )}
    </div>
  );
};

export default TrackList;
