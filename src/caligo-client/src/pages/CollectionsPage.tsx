import { useSearchParams } from 'react-router-dom';
import { useGetCollectionsQuery } from '../api/collectionsApi';
import { catalogError } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { useCollections } from '../store/hooks';
import { collectionPath } from '../thesaurarium';

const titleCount = (titles: number, songs: number): string =>
  `${titles} ${titles === songs ? 'track' : 'title'}${titles === 1 ? '' : 's'}`;

const CollectionsPage = () => {
  const { createCollection } = useCollections();
  const [params] = useSearchParams();
  const query = (params.get('query') ?? '').toLowerCase();

  const { data, error: loadError, isLoading } = useGetCollectionsQuery();

  const collections = isLoading ? null : data ?? [];
  const error = loadError ? catalogError(loadError, 'Could not load your collections.') : null;

  const shown = (collections ?? []).filter(collection => collection.name.toLowerCase().includes(query));

  return (
    <div>
      <ThesaurariumNav title="My Collections" />

      <div className="collections-toolbar">
        <button type="button" onClick={createCollection}>＋ Create Playlist</button>
      </div>

      {collections === null && <p className="status-line">Loading...</p>}
      {error && <p className="form-error">{error}</p>}
      {collections !== null && !error && shown.length === 0 && (
        <p className="status-line">{query ? 'Nothing found.' : 'No collections yet.'}</p>
      )}

      {shown.length > 0 && (
        <ul className="album-grid">
          {shown.map(collection => (
            <li key={collection.collectionId}>
              <CoverCard
                to={collectionPath(collection.collectionId)}
                cover={collection.coverUri}
                kind="Collection"
                title={collection.name}
                sub={titleCount(collection.titles, collection.songs)}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};

export default CollectionsPage;
