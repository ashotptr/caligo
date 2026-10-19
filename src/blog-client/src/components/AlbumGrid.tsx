import { Link } from 'react-router-dom';
import { yearOf } from '../catalog';
import Artwork from './Artwork';
import type { AlbumSummary } from '../types';

const AlbumGrid = ({ albums }: { albums: AlbumSummary[] }) => (
  <ul className="album-grid">
    {albums.map(album => (
      <li key={album.albumId}>
        <Link to={`/albums/${album.albumId}`} className="card album-card">
          <span className="album-cover">
            <Artwork src={album.coverUri} className="album-art" placeholder="♪" />
          </span>
          <span className="card-title">{album.title}</span>
          <span className="card-sub">
            {[album.artists.join(', '), yearOf(album.releaseDate), `${album.songs} song${album.songs === 1 ? '' : 's'}`]
              .filter(part => part.length > 0)
              .join(' · ')}
          </span>
        </Link>
      </li>
    ))}
  </ul>
);

export default AlbumGrid;
