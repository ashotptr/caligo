import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError } from '../catalog';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { artistPath } from '../thesaurarium';
import type { ArtistSummary } from '../types';

const ArtistsPage = () => {
  const [params] = useSearchParams();
  const activeQuery = params.get('query') ?? '';

  const [artists, setArtists] = useState<ArtistSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setLoading(true);
    setError(null);

    api
      .get<ArtistSummary[]>('/api/artists', { params: { query: activeQuery || undefined } })
      .then(response => {
        if (!cancelled) setArtists(response.data);
      })
      .catch(err => {
        if (!cancelled) setError(catalogError(err, 'Could not load the artists.'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeQuery]);

  return (
    <div>
      <ThesaurariumNav />

      {loading && <p className="status-line">Loading artists…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && artists.length === 0 && (
        <p className="status-line">
          {activeQuery ? `No artist matches “${activeQuery}”.` : 'No artists yet. An administrator can import the music library from the Insights page.'}
        </p>
      )}

      <ul className="dashboard-list">
        {artists.map(artist => (
          <li key={artist.artistId}>
            <Link to={artistPath(artist.artistId)}>{artist.name}</Link>
            <span className="post-meta">{artist.songs} song{artist.songs === 1 ? '' : 's'}</span>
          </li>
        ))}
      </ul>
    </div>
  );
};

export default ArtistsPage;
