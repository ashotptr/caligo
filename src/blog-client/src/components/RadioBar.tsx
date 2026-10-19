import { Link } from 'react-router-dom';
import { useRadio } from '../contexts/RadioContext';
import { pathLabel } from '../radio';
import ListenLog from './ListenLog';

const STATUS: Record<string, { label: string; tone: string }> = {
  off: { label: 'Off air', tone: '' },
  starting: { label: 'Tuning in', tone: 'waiting' },
  playing: { label: 'Live', tone: 'live' },
  buffering: { label: 'Buffering', tone: 'waiting' },
  retrying: { label: 'Reconnecting', tone: 'waiting' }
};

const RadioBar = () => {
  const { state, path, now, songId, toggle } = useRadio();
  const status = STATUS[state] ?? STATUS.off;
  const artist = now && now.artist && now.artist !== 'Unknown' ? now.artist : '';
  const album = now && now.album && now.album !== 'Unknown' ? now.album : '';

  return (
    <div className="player-bar">
      <div className="player-info">
        <div className="np-title">
          {now ? (songId !== null ? <Link to={`/catalog/${songId}`}>{now.title}</Link> : now.title) : 'Caligo Radio'}
        </div>
        <div className="np-album">
          {now
            ? [artist, album].filter(part => part.length > 0).join(' · ') || 'Unknown artist'
            : state === 'off' ? 'Press play to tune in' : 'Waiting for the first song'}
        </div>
      </div>

      <div className="player-controls">
        <button
          type="button"
          className="play-toggle"
          onClick={toggle}
          aria-label={state === 'off' ? 'Play the radio' : 'Stop the radio'}
        >
          {state === 'off' ? '▶' : '■'}
        </button>

        <span className={`live-chip ${status.tone}`}>
          <span className="live-dot" />
          {status.label}
        </span>

        {now?.isRequest && <span className="badge">request</span>}
      </div>

      <div className="player-extra">
        {songId !== null && <ListenLog contentId={songId} />}
        {state !== 'off' && (
          <span className="status-line">{pathLabel(path)}</span>
        )}
        <Link to="/listen" className="player-more">Radio</Link>
      </div>
    </div>
  );
};

export default RadioBar;
