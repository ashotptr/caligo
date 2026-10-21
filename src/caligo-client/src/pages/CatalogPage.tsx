import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { KINDS, catalogError } from '../catalog';
import ContentCard from '../components/ContentCard';
import InsightPanels from '../components/InsightPanels';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { TITLES } from '../thesaurarium';
import type { ContentSummary, Named, PagedResult } from '../types';

const CatalogPage = () => {
  const [params, setParams] = useSearchParams();

  const kind = params.get('kind') ?? '';
  const genreId = params.get('genreId') ?? '';
  const activeQuery = params.get('query') ?? '';

  const [items, setItems] = useState<ContentSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [genres, setGenres] = useState<Named[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const request = useRef(0);

  useEffect(() => {
    api
      .get<Named[]>('/api/genres')
      .then(response => setGenres(response.data))
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    const current = ++request.current;

    setLoading(true);
    setError(null);

    api
      .get<PagedResult<ContentSummary>>('/api/content', {
        params: { kind: kind || undefined, genreId: genreId || undefined, query: activeQuery || undefined }
      })
      .then(response => {
        if (current !== request.current) return;
        setItems(response.data.items);
        setCursor(response.data.nextCursor);
      })
      .catch(err => {
        if (current !== request.current) return;
        setItems([]);
        setCursor(null);
        setError(catalogError(err, 'Could not load the Thesaurarium. Is the API running?'));
      })
      .finally(() => {
        if (current === request.current) setLoading(false);
      });
  }, [kind, genreId, activeQuery]);

  const change = (changes: Record<string, string>) => {
    const next = new URLSearchParams(params);

    for (const [key, value] of Object.entries(changes)) {
      if (value) {
        next.set(key, value);
      } else {
        next.delete(key);
      }
    }

    setParams(next);
  };

  const loadMore = async () => {
    if (!cursor || loadingMore) {
      return;
    }

    const current = request.current;

    setLoadingMore(true);
    try {
      const response = await api.get<PagedResult<ContentSummary>>('/api/content', {
        params: { kind: kind || undefined, genreId: genreId || undefined, query: activeQuery || undefined, cursor }
      });
      if (current !== request.current) return;
      setItems(previous => [...previous, ...response.data.items]);
      setCursor(response.data.nextCursor);
    } catch (err) {
      setError(catalogError(err, 'Could not load more titles.'));
    } finally {
      setLoadingMore(false);
    }
  };


  const filtered = kind !== '' || genreId !== '' || activeQuery !== '';

  return (
    <div>
      <ThesaurariumNav />

      {activeQuery && (
        <p className="search-summary">
          Titles matching “{activeQuery}” · <Link to={TITLES}>Clear</Link>
        </p>
      )}

      <div className="catalog-filters">
        <div className="tabs" role="group" aria-label="Kind of title">
          {KINDS.map(option => (
            <button
              key={option.value}
              type="button"
              aria-pressed={kind === option.value}
              onClick={() => change({ kind: option.value })}
            >
              {option.label}
            </button>
          ))}
        </div>

        <label className="catalog-genre">
          Genre
          <select value={genreId} onChange={e => change({ genreId: e.target.value })}>
            <option value="">Any</option>
            {genres.map(genre => (
              <option key={genre.id} value={genre.id}>{genre.name}</option>
            ))}
          </select>
        </label>
      </div>

      {!filtered && <InsightPanels />}

      {loading && <p className="status-line">Loading titles…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && items.length === 0 && (
        <p className="status-line">{filtered ? 'Nothing matches those filters.' : 'The Thesaurarium is empty.'}</p>
      )}

      {items.length > 0 && (
        <div className="card-grid">
          {items.map(item => (
            <ContentCard key={item.contentId} content={item} />
          ))}
        </div>
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

export default CatalogPage;
