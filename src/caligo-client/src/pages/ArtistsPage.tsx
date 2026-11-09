import { useSearchParams } from 'react-router-dom';
import { useGetArtistsQuery } from '../api/catalogApi';
import { catalogError } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { artistPath } from '../thesaurarium';

const ArtistsPage = () => {
  const [params] = useSearchParams();
  const activeQuery = params.get('query') ?? '';

  const { currentData: found, error: loadError, isFetching } = useGetArtistsQuery(activeQuery);
  const artists = found ?? [];
  const loading = isFetching && !found;
  const error = loadError ? catalogError(loadError, 'Could not load the artists.') : null;

  return (
    <div>
      <ThesaurariumNav />

      {loading && <p className="status-line">Loading artists…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && artists.length === 0 && (
        <p className="status-line">
          {activeQuery ? `No artist matches “${activeQuery}”.` : 'No artists yet.'}
        </p>
      )}

      {artists.length > 0 && (
        <ul className="album-grid">
          {artists.map(artist => (
            <li key={artist.artistId}>
              <CoverCard
                to={artistPath(artist.artistId)}
                cover={artist.coverUri}
                kind="Artist"
                title={artist.name}
                sub="Artist"
                meta={`${artist.songs} song${artist.songs === 1 ? '' : 's'}`}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};

export default ArtistsPage;
