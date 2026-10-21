import { Link } from 'react-router-dom';
import { usePlayer, usePlayerClock } from '../contexts/PlayerContext';
import { pathLabel } from '../radio';
import { titlePath } from '../thesaurarium';
import ListenLog from './ListenLog';

const STATUS: Record<string, { label: string; tone: string }> = {
  off: { label: 'Off air', tone: '' },
  starting: { label: 'Tuning in', tone: 'waiting' },
  playing: { label: 'Live', tone: 'live' },
  buffering: { label: 'Buffering', tone: 'waiting' },
  retrying: { label: 'Reconnecting', tone: 'waiting' },
  failed: { label: 'Failed', tone: 'error' }
};

const clockText = (seconds: number): string => {
  const whole = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;

  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
};

const known = (value: string | undefined): string => (value && value !== 'Unknown' ? value : '');

const TrackSeek = () => {
  const { seek } = usePlayer();
  const { time, duration } = usePlayerClock();

  return (
    <div className="player-seek">
      <span>{clockText(time)}</span>
      <input
        type="range"
        min={0}
        max={duration || 0}
        step={0.5}
        value={Math.min(time, duration || 0)}
        disabled={!duration}
        aria-label="Position in the track"
        onChange={event => seek(Number(event.target.value))}
      />
      <span>{clockText(duration)}</span>
    </div>
  );
};

const Volume = () => {
  const { volume, setVolume } = usePlayer();

  return (
    <input
      type="range"
      className="volume-control"
      min={0}
      max={1}
      step={0.05}
      value={volume}
      aria-label="Volume"
      onChange={event => setVolume(Number(event.target.value))}
    />
  );
};

const PlayerBar = () => {
  const {
    mode, radioState, radioDetail, path, now, songId, current, queue, index, paused, trackFailed,
    playRadio, stopRadio, togglePause, next, previous
  } = usePlayer();

  if (mode === 'track' && current) {
    const subtitle = [known(current.artist), known(current.album)].filter(part => part.length > 0).join(' · ');

    return (
      <div className="player-bar">
        <div className="player-info">
          <div className="np-title">
            {current.contentId !== null ? <Link to={titlePath(current.contentId)}>{current.title}</Link> : current.title}
          </div>
          <div className="np-album">{trackFailed ? 'This track could not be played.' : subtitle}</div>
        </div>

        <div className="player-controls">
          <button type="button" className="player-skip" onClick={previous} aria-label="Previous track">⏮</button>
          <button type="button" className="play-toggle" onClick={togglePause} aria-label={paused ? 'Play' : 'Pause'}>
            {paused ? '▶' : '❚❚'}
          </button>
          <button
            type="button"
            className="player-skip"
            onClick={next}
            disabled={index >= queue.length - 1}
            aria-label="Next track"
          >
            ⏭
          </button>
          <TrackSeek />
        </div>

        <div className="player-extra">
          <Volume />
          {current.contentId !== null && <ListenLog contentId={current.contentId} />}
          <Link to="/listen" className="player-more">Radio</Link>
        </div>
      </div>
    );
  }

  const on = mode === 'radio';
  const status = STATUS[on ? radioState : 'off'] ?? STATUS.off;
  const subtitle = now
    ? [known(now.artist), known(now.album)].filter(part => part.length > 0).join(' · ') || 'Unknown artist'
    : !on ? 'Press play to tune in' : radioState === 'failed' ? radioDetail ?? 'This way did not play' : 'Waiting for the first song';

  return (
    <div className="player-bar">
      <div className="player-info">
        <div className="np-title">
          {on && now ? (songId !== null ? <Link to={titlePath(songId)}>{now.title}</Link> : now.title) : 'Caligo Radio'}
        </div>
        <div className="np-album">{on ? subtitle : 'Press play to tune in'}</div>
      </div>

      <div className="player-controls">
        <button
          type="button"
          className="play-toggle"
          onClick={() => (on ? stopRadio() : playRadio())}
          aria-label={on ? 'Stop the radio' : 'Play the radio'}
        >
          {on ? '■' : '▶'}
        </button>

        <span className={`live-chip ${status.tone}`}>
          <span className="live-dot" />
          {status.label}
        </span>

        {on && now?.isRequest && <span className="badge">request</span>}
      </div>

      <div className="player-extra">
        <Volume />
        {on && songId !== null && <ListenLog contentId={songId} />}
        {on && path !== null && radioState !== 'failed' && <span className="status-line">{pathLabel(path)}</span>}
        <Link to="/listen" className="player-more">Radio</Link>
      </div>
    </div>
  );
};

export default PlayerBar;
