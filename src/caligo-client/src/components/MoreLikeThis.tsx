import { Link } from 'react-router-dom';
import { useLookupSongQuery } from '../api/catalogApi';
import { usePlayer } from '../contexts/PlayerContext';
import { titlePath } from '../thesaurarium';
import type { SimilarSong } from '../types';

const SimilarRow = ({ song }: { song: SimilarSong }) => {
  const { playTracks } = usePlayer();
  const { currentData: found } = useLookupSongQuery(
    { artist: song.artist ?? '', title: song.title ?? '' },
    { skip: !song.artist || !song.title }
  );
  const title = song.title ?? song.path;

  const play = () => {
    if (found) {
      playTracks([{ contentId: found.contentId, title, artist: song.artist ?? '', album: '', audioUri: song.uri, coverUri: null }], 0);
    }
  };

  return (
    <li>
      <span>
        {found ? <Link to={titlePath(found.contentId)}>{title}</Link> : title}
        {song.artist && <span className="post-meta"> · {song.artist}</span>}
      </span>
      <button type="button" className="btn-secondary" title="Play" disabled={!found} onClick={play}>▶</button>
    </li>
  );
};

const MoreLikeThis = ({ songs }: { songs: SimilarSong[] }) => (
  <ol className="trending-list similar-songs">
    {songs.map(song => <SimilarRow key={song.path} song={song} />)}
  </ol>
);

export default MoreLikeThis;
