import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, statusOf } from '../catalog';
import BackLink from '../components/BackLink';
import TrackList from '../components/TrackList';
import type { TrackRow } from '../components/TrackList';
import { useCollections } from '../contexts/CollectionsContext';
import { useToast } from '../contexts/ToastContext';
import { COLLECTIONS } from '../thesaurarium';
import type { CollectionDetail } from '../types';

const CollectionPage = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const toast = useToast();
  const { version, changed } = useCollections();

  const [collection, setCollection] = useState<CollectionDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState('');
  const [renameError, setRenameError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setError(null);

    api
      .get<CollectionDetail>(`/api/collections/${id}`)
      .then(response => {
        if (!cancelled) setCollection(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? 'This collection does not exist.' : catalogError(err, 'Could not load the collection.'));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id, version]);

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
      await api.put(`/api/collections/${collection.collectionId}`, { name: cleaned });
      setRenaming(false);
      setRenameError(null);
      changed();
    } catch (err) {
      setRenameError(catalogError(err, 'Could not rename the collection.'));
    }
  };

  const remove = async () => {
    if (!window.confirm(`Delete the collection ${collection.name}?`)) {
      return;
    }

    try {
      await api.delete(`/api/collections/${collection.collectionId}`);
      changed();
      navigate(COLLECTIONS);
    } catch (err) {
      toast(catalogError(err, 'Could not delete the collection.'));
    }
  };

  const removeTitle = async (row: TrackRow) => {
    try {
      await api.delete(`/api/collections/${collection.collectionId}/titles/${row.contentId}`);
      setCollection(previous => previous && {
        ...previous,
        titles: previous.titles.filter(title => title.contentId !== row.contentId)
      });
      toast(`Removed from ${collection.name}`);
    } catch (err) {
      toast(catalogError(err, 'Could not remove the title.'));
    }
  };

  return (
    <article className="post-detail">
      <BackLink fallback={COLLECTIONS} />

      {renaming ? (
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
      ) : (
        <h1>{collection.name}</h1>
      )}
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

      <section className="catalog-section">
        <TrackList tracks={rows} album={collection.name} download={collection.name} inCollection onRemove={removeTitle} />
      </section>
    </article>
  );
};

export default CollectionPage;
