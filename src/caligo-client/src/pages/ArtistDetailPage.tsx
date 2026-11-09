import { Link, useParams } from 'react-router-dom';
import { useGetArtistQuery } from '../api/catalogApi';
import { catalogError, formatDay, statusOf } from '../catalog';
import AlbumGrid from '../components/AlbumGrid';
import DetailHeader from '../components/DetailHeader';
import TrackList from '../components/TrackList';
import { ARTISTS, singlePath } from '../thesaurarium';

const ArtistDetailPage = () => {
  const { id = '' } = useParams<{ id: string }>();

  const { currentData: artist, error: loadError } = useGetArtistQuery(id);
  const error = loadError
    ? statusOf(loadError) === 404 ? 'This artist is not in the Thesaurarium.' : catalogError(loadError, 'Could not load the artist.')
    : null;

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to={ARTISTS}>← All artists</Link>
      </div>
    );
  }

  if (!artist) {
    return <p className="status-line">Loading…</p>;
  }

  const facts = [artist.country, artist.formedDate ? `since ${formatDay(artist.formedDate)}` : null]
    .filter((fact): fact is string => !!fact);

  return (
    <article className="post-detail">
      <Link to={ARTISTS} className="back-link">← All artists</Link>

      <DetailHeader
        cover={artist.pictureUri || artist.albums.find(album => album.coverUri)?.coverUri}
        title={artist.name}
        kind="Artist"
      >
        <p className="post-meta">
          {['Artist', ...facts, `${artist.songs.length} song${artist.songs.length === 1 ? '' : 's'}`].join(' · ')}
        </p>
      </DetailHeader>

      {artist.biography && <p className="content-description">{artist.biography}</p>}

      {artist.albums.length > 0 && (
        <section className="catalog-section">
          <h2>Albums</h2>
          <AlbumGrid albums={artist.albums} />
        </section>
      )}

      {artist.singles.length > 0 && (
        <section className="catalog-section">
          <h2>Singles</h2>
          <ul className="review-list">
            {artist.singles.map(single => (
              <li key={single.id}>
                <Link to={singlePath(single.id)}>{single.name}</Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="catalog-section">
        <h2>Songs</h2>
        <TrackList tracks={artist.songs} numbered={false} />
      </section>
    </article>
  );
};

export default ArtistDetailPage;
