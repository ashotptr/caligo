import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { useCollections } from '../contexts/CollectionsContext';
import { collectionPath } from '../thesaurarium';
import type { CollectionSummary } from '../types';

const titleCount = (titles: number, songs: number): string =>
  `${titles} ${titles === songs ? 'track' : 'title'}${titles === 1 ? '' : 's'}`;

const CollectionsPage = () => {
  const { version, createCollection } = useCollections();
  const [params] = useSearchParams();
  const query = (params.get('query') ?? '').toLowerCase();

  const [collections, setCollections] = useState<CollectionSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setError(null);

    api
      .get<CollectionSummary[]>('/api/collections')
      .then(response => {
        if (!cancelled) setCollections(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setCollections([]);
          setError(catalogError(err, 'Could not load your collections.'));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [version]);

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
