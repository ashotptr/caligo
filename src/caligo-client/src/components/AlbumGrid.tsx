import { yearOf } from '../catalog';
import { albumPath } from '../thesaurarium';
import CoverCard from './CoverCard';
import type { AlbumSummary } from '../types';

const AlbumGrid = ({ albums }: { albums: AlbumSummary[] }) => (
  <ul className="album-grid">
    {albums.map(album => (
      <li key={album.albumId}>
        <CoverCard
          to={albumPath(album.albumId)}
          cover={album.coverUri}
          placeholder="No Cover"
          title={album.title}
          sub={album.artists.join(', ') || 'Unknown'}
          meta={[yearOf(album.releaseDate), `${album.songs} song${album.songs === 1 ? '' : 's'}`]
            .filter(part => part.length > 0)
            .join(' · ')}
        />
      </li>
    ))}
  </ul>
);

export default AlbumGrid;
