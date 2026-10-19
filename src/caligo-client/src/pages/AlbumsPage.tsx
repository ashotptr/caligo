import { useEffect, useState } from 'react';
import api from '../api/axios';
import { catalogError } from '../catalog';
import AlbumGrid from '../components/AlbumGrid';
import CatalogNav from '../components/CatalogNav';
import type { AlbumSummary } from '../types';

const AlbumsPage = () => {
  const [albums, setAlbums] = useState<AlbumSummary[]>([]);
  const [query, setQuery] = useState('');
  const [activeQuery, setActiveQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setLoading(true);
    setError(null);

    api
      .get<AlbumSummary[]>('/api/albums', { params: { query: activeQuery || undefined } })
      .then(response => {
        if (!cancelled) setAlbums(response.data);
      })
      .catch(err => {
        if (!cancelled) setError(catalogError(err, 'Could not load the albums.'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeQuery]);

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    setActiveQuery(query.trim());
  };

  return (
    <div>
      <CatalogNav />

      <form onSubmit={handleSearch} className="search-bar">
        <input
          type="search"
          placeholder="Search albums…"
          value={query}
          maxLength={100}
          onChange={e => setQuery(e.target.value)}
        />
        <button type="submit">Search</button>
        {activeQuery && (
          <button
            type="button"
            className="link-button"
            onClick={() => {
              setQuery('');
              setActiveQuery('');
            }}
          >
            Clear
          </button>
        )}
      </form>

      {loading && <p className="status-line">Loading albums…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && albums.length === 0 && (
        <p className="status-line">
          {activeQuery ? `No album matches “${activeQuery}”.` : 'No albums yet. An administrator can import the music library from the Insights page.'}
        </p>
      )}

      <AlbumGrid albums={albums} />
    </div>
  );
};

export default AlbumsPage;
