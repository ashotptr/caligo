import { useSearchParams } from 'react-router-dom';
import { useGetReleasesInfiniteQuery } from '../api/catalogApi';
import { catalogError, yearOf } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { releasePath } from '../thesaurarium';

const ThesaurariumPage = () => {
  const [params] = useSearchParams();
  const query = params.get('query') ?? '';
  const artistId = params.get('artistId') ?? '';

  const list = useGetReleasesInfiniteQuery({ query: query || undefined, artistId: artistId || undefined });

  const releases = list.currentData?.pages.flatMap(page => page.items) ?? [];
  const loading = list.isFetching && !list.currentData;
  const error = list.isFetchNextPageError
    ? catalogError(list.error, 'Could not load more albums.')
    : list.isError
      ? catalogError(list.error, 'Could not load the Thesaurarium.')
      : null;

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
                kind={release.kind === 'album' ? 'Album' : 'Single'}
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

export default ThesaurariumPage;
