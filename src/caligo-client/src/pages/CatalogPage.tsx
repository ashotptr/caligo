import { Link, useSearchParams } from 'react-router-dom';
import { useGetContentListInfiniteQuery, useGetGenresQuery } from '../api/catalogApi';
import { KINDS, catalogError } from '../catalog';
import ContentCard from '../components/ContentCard';
import InsightPanels from '../components/InsightPanels';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { TITLES } from '../thesaurarium';
import type { ContentKind } from '../types';

const CatalogPage = () => {
  const [params, setParams] = useSearchParams();

  const kind = params.get('kind') ?? '';
  const genreId = params.get('genreId') ?? '';
  const activeQuery = params.get('query') ?? '';

  const { data: genres = [] } = useGetGenresQuery();
  const list = useGetContentListInfiniteQuery({
    kind: (kind || undefined) as ContentKind | undefined,
    genreId: genreId || undefined,
    query: activeQuery || undefined
  });

  const items = list.currentData?.pages.flatMap(page => page.items) ?? [];
  const loading = list.isFetching && !list.currentData;
  const error = list.isFetchNextPageError
    ? catalogError(list.error, 'Could not load more titles.')
    : list.isError
      ? catalogError(list.error, 'Could not load the Thesaurarium.')
      : null;

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
        <ul className="album-grid">
          {items.map(item => (
            <li key={item.contentId}>
              <ContentCard content={item} />
            </li>
          ))}
        </ul>
      )}

      {!loading && list.hasNextPage && (
        <div className="load-more">
          <button type="button" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
            {list.isFetchingNextPage ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </div>
  );
};

export default CatalogPage;
