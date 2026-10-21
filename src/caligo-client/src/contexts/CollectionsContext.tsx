import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import api from '../api/axios';
import { catalogError } from '../catalog';
import Modal from '../components/Modal';
import { COLLECTIONS } from '../thesaurarium';
import type { AddTitlesResult, CollectionSummary } from '../types';
import { useToast } from './ToastContext';

interface Pending {
  contentIds: number[];
  title: string;
}

interface CollectionsContextType {
  version: number;
  addToCollection: (contentIds: number[], title: string) => void;
  createCollection: () => void;
  changed: () => void;
}

const CollectionsContext = createContext<CollectionsContextType | undefined>(undefined);

const AddDialog = ({ pending, onDone }: { pending: Pending; onDone: (added: boolean) => void }) => {
  const toast = useToast();
  const [collections, setCollections] = useState<CollectionSummary[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;

    api
      .get<CollectionSummary[]>('/api/collections')
      .then(response => {
        if (!cancelled) setCollections(response.data);
      })
      .catch(() => {
        if (!cancelled) setFailed(true);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const add = async (collection: CollectionSummary) => {
    if (saving) {
      return;
    }

    setSaving(true);

    try {
      const { data } = await api.post<AddTitlesResult>(`/api/collections/${collection.collectionId}/titles`, {
        contentIds: pending.contentIds
      });

      toast(data.added > 0 ? `Added to ${collection.name}` : `Already in ${collection.name}`);
      onDone(data.added > 0);
    } catch {
      toast('Error adding track');
      onDone(false);
    }
  };

  return (
    <Modal title="Add to Collection" onClose={() => onDone(false)}>
      <p className="modal-note">Adding: {pending.title}</p>

      <div className="playlist-select-list">
        {collections === null && !failed && <p className="playlist-empty">Loading...</p>}
        {failed && <p className="playlist-empty">Could not load your collections.</p>}
        {collections?.length === 0 && <p className="playlist-empty">No collections found. Create one first!</p>}
        {collections?.map(collection => (
          <button
            key={collection.collectionId}
            type="button"
            className="playlist-option"
            disabled={saving}
            onClick={() => add(collection)}
          >
            {collection.name}
          </button>
        ))}
      </div>

      <button type="button" className="btn-secondary modal-wide" onClick={() => onDone(false)}>Cancel</button>
    </Modal>
  );
};

const CreateDialog = ({ onDone }: { onDone: (created: boolean) => void }) => {
  const toast = useToast();
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const create = async (event: React.FormEvent) => {
    event.preventDefault();

    const cleaned = name.trim();

    if (!cleaned || saving) {
      return;
    }

    setSaving(true);
    setError(null);

    try {
      await api.post('/api/collections', { name: cleaned });
      toast('Collection created');
      onDone(true);
    } catch (err) {
      setError(catalogError(err, 'Could not create the collection.'));
      setSaving(false);
    }
  };

  return (
    <Modal title="New Collection" onClose={() => onDone(false)}>
      <form onSubmit={create}>
        <label className="input-group">
          <span>Collection Name</span>
          <input
            type="text"
            value={name}
            maxLength={100}
            placeholder="e.g. Late Night"
            autoFocus
            onChange={event => setName(event.target.value)}
          />
        </label>

        {error && <p className="form-error">{error}</p>}

        <div className="modal-actions">
          <button type="button" className="btn-secondary" onClick={() => onDone(false)}>Cancel</button>
          <button type="submit" disabled={saving || name.trim().length === 0}>Create</button>
        </div>
      </form>
    </Modal>
  );
};

export const CollectionsProvider = ({ children }: { children: ReactNode }) => {
  const navigate = useNavigate();
  const [version, setVersion] = useState(0);
  const [pending, setPending] = useState<Pending | null>(null);
  const [creating, setCreating] = useState(false);

  const changed = useCallback(() => setVersion(previous => previous + 1), []);

  const addToCollection = useCallback((contentIds: number[], title: string) => {
    if (contentIds.length > 0) {
      setPending({ contentIds, title });
    }
  }, []);

  const createCollection = useCallback(() => setCreating(true), []);

  const added = useCallback((done: boolean) => {
    setPending(null);

    if (done) changed();
  }, [changed]);

  const created = useCallback((done: boolean) => {
    setCreating(false);

    if (done) {
      changed();
      navigate(COLLECTIONS);
    }
  }, [changed, navigate]);

  const value = useMemo(
    () => ({ version, addToCollection, createCollection, changed }),
    [version, addToCollection, createCollection, changed]
  );

  return (
    <CollectionsContext.Provider value={value}>
      {children}
      {pending && <AddDialog pending={pending} onDone={added} />}
      {creating && <CreateDialog onDone={created} />}
    </CollectionsContext.Provider>
  );
};

export const useCollections = (): CollectionsContextType => {
  const context = useContext(CollectionsContext);

  if (!context) {
    throw new Error('useCollections must be used within a CollectionsProvider');
  }

  return context;
};
