import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, statusOf } from '../catalog';
import AlbumGrid from '../components/AlbumGrid';
import Artwork from '../components/Artwork';
import TrackList from '../components/TrackList';
import type { ArtistDetail } from '../types';

const ArtistDetailPage = () => {
  const { id } = useParams<{ id: string }>();

  const [artist, setArtist] = useState<ArtistDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setArtist(null);
    setError(null);

    api
      .get<ArtistDetail>(`/api/artists/${id}`)
      .then(response => {
        if (!cancelled) setArtist(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? 'This artist is not in the catalog.' : catalogError(err, 'Could not load the artist.'));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id]);

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to="/artists">← All artists</Link>
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
      <Link to="/artists" className="back-link">← All artists</Link>

      <div className="portrait-header">
        <Artwork src={artist.pictureUri} className="portrait" />
        <div>
          <h1>{artist.name}</h1>
          <p className="post-meta">
            {['Artist', ...facts, `${artist.songs.length} song${artist.songs.length === 1 ? '' : 's'}`].join(' · ')}
          </p>
        </div>
      </div>

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
                <Link to={`/singles/${single.id}`}>{single.name}</Link>
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
