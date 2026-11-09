import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
  useDeleteCollectionMutation,
  useGetCollectionQuery,
  useRemoveTitleMutation,
  useRenameCollectionMutation
} from '../api/collectionsApi';
import { catalogError, statusOf } from '../catalog';
import BackLink from '../components/BackLink';
import DetailHeader from '../components/DetailHeader';
import TrackList from '../components/TrackList';
import type { TrackRow } from '../components/TrackList';
import { useToast } from '../store/hooks';
import { COLLECTIONS } from '../thesaurarium';

const CollectionPage = () => {
  const { id = '' } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const toast = useToast();

  const { currentData: collection, error: loadError } = useGetCollectionQuery(id);
  const [renameCollection] = useRenameCollectionMutation();
  const [deleteCollection] = useDeleteCollectionMutation();
  const [removeTitleCall] = useRemoveTitleMutation();

  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState('');
  const [renameError, setRenameError] = useState<string | null>(null);

  const error = loadError
    ? statusOf(loadError) === 404 ? 'This collection does not exist.' : catalogError(loadError, 'Could not load the collection.')
    : null;

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <BackLink fallback={COLLECTIONS} />
      </div>
    );
  }

  if (!collection) {
    return <p className="status-line">Loading...</p>;
  }

  const rows: TrackRow[] = collection.titles.map(title => ({
    contentId: title.contentId,
    songId: 0,
    title: title.title,
    duration: title.duration,
    discNumber: null,
    trackNumber: null,
    audioUri: title.audioUri,
    artists: title.artists,
    kind: title.kind,
    coverUri: title.coverUri
  }));

  const rename = async (event: React.FormEvent) => {
    event.preventDefault();

    const cleaned = name.trim();

    if (!cleaned) {
      return;
    }

    try {
      await renameCollection({ collectionId: collection.collectionId, name: cleaned }).unwrap();
      setRenaming(false);
      setRenameError(null);
    } catch (err) {
      setRenameError(catalogError(err, 'Could not rename the collection.'));
    }
  };

  const remove = async () => {
    if (!window.confirm(`Delete the collection ${collection.name}?`)) {
      return;
    }

    try {
      await deleteCollection(collection.collectionId).unwrap();
      navigate(COLLECTIONS);
    } catch (err) {
      toast(catalogError(err, 'Could not delete the collection.'));
    }
  };

  const removeTitle = async (row: TrackRow) => {
    try {
      await removeTitleCall({ collectionId: collection.collectionId, contentId: row.contentId }).unwrap();
      toast(`Removed from ${collection.name}`);
    } catch (err) {
      toast(catalogError(err, 'Could not remove the title.'));
    }
  };

  return (
    <article className="post-detail">
      <BackLink fallback={COLLECTIONS} />

      <DetailHeader
        cover={collection.titles.find(title => title.coverUri)?.coverUri}
        title={collection.name}
        kind="Collection"
        heading={renaming ? (
          <form className="collection-rename" onSubmit={rename}>
            <input
              type="text"
              value={name}
              maxLength={100}
              aria-label="Collection Name"
              autoFocus
              onChange={event => setName(event.target.value)}
            />
            <button type="submit" disabled={name.trim().length === 0}>Save</button>
            <button type="button" className="btn-secondary" onClick={() => setRenaming(false)}>Cancel</button>
          </form>
        ) : undefined}
      >
        {renameError && <p className="form-error">{renameError}</p>}

        <p className="post-meta collection-tools">
          Collection · {collection.titles.length} title{collection.titles.length === 1 ? '' : 's'}
          {' · '}
          <button
            type="button"
            className="link-button"
            onClick={() => {
              setName(collection.name);
              setRenameError(null);
              setRenaming(true);
            }}
          >
            Rename
          </button>
          {' · '}
          <button type="button" className="link-button danger" onClick={remove}>Delete</button>
        </p>
      </DetailHeader>

      <section className="catalog-section">
        <TrackList tracks={rows} album={collection.name} download={collection.name} inCollection onRemove={removeTitle} />
      </section>
    </article>
  );
};

export default CollectionPage;
