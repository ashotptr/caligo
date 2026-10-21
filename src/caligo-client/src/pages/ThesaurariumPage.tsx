import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, yearOf } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { releasePath } from '../thesaurarium';
import type { PagedResult, ReleaseSummary } from '../types';

const ThesaurariumPage = () => {
  const [params] = useSearchParams();
  const query = params.get('query') ?? '';
  const artistId = params.get('artistId') ?? '';

  const [releases, setReleases] = useState<ReleaseSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const request = useRef(0);

  useEffect(() => {
    const current = ++request.current;

    setLoading(true);
    setError(null);

    api
      .get<PagedResult<ReleaseSummary>>('/api/releases', {
        params: { query: query || undefined, artistId: artistId || undefined }
      })
      .then(response => {
        if (current !== request.current) return;
        setReleases(response.data.items);
        setCursor(response.data.nextCursor);
      })
      .catch(err => {
        if (current !== request.current) return;
        setReleases([]);
        setCursor(null);
        setError(catalogError(err, 'Could not load the Thesaurarium.'));
      })
      .finally(() => {
        if (current === request.current) setLoading(false);
      });
  }, [query, artistId]);

  const loadMore = async () => {
    if (!cursor || loadingMore) {
      return;
    }

    const current = request.current;

    setLoadingMore(true);

    try {
      const response = await api.get<PagedResult<ReleaseSummary>>('/api/releases', {
        params: { query: query || undefined, artistId: artistId || undefined, cursor }
      });

      if (current !== request.current) return;
      setReleases(previous => [...previous, ...response.data.items]);
      setCursor(response.data.nextCursor);
    } catch (err) {
      setError(catalogError(err, 'Could not load more albums.'));
    } finally {
      setLoadingMore(false);
    }
  };

  const filtered = query !== '' || artistId !== '';

  return (
    <div>
      <ThesaurariumNav />

      {loading && <p className="status-line">Loading...</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && releases.length === 0 && (
        <p className="status-line">
          {filtered ? 'Nothing found.' : 'No albums yet.'}
        </p>
      )}

      {!loading && releases.length > 0 && (
        <ul className="album-grid">
          {releases.map(release => (
            <li key={`${release.kind}-${release.id}`}>
              <CoverCard
                to={releasePath(release.kind, release.id)}
                cover={release.coverUri}
                placeholder="No Cover"
                title={release.title}
                sub={release.artists.join(', ') || 'Unknown'}
                meta={[
                  release.kind === 'album' ? 'Album' : 'Single',
                  yearOf(release.releaseDate),
                  `${release.songs} song${release.songs === 1 ? '' : 's'}`
                ].filter(part => part.length > 0).join(' · ')}
              />
            </li>
          ))}
        </ul>
      )}

      {!loading && cursor && (
        <div className="load-more">
          <button type="button" onClick={loadMore} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </div>
  );
};

export default ThesaurariumPage;
