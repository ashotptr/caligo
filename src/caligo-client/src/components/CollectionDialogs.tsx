import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAddTitlesMutation, useCreateCollectionMutation, useGetCollectionsQuery } from '../api/collectionsApi';
import { catalogError } from '../catalog';
import { addClosed, createClosed } from '../store/collectionsSlice';
import type { PendingAdd } from '../store/collectionsSlice';
import { useAppDispatch, useAppSelector, useToast } from '../store/hooks';
import { COLLECTIONS } from '../thesaurarium';
import type { CollectionSummary } from '../types';
import Modal from './Modal';

const AddDialog = ({ pending, onDone }: { pending: PendingAdd; onDone: () => void }) => {
  const toast = useToast();
  const { data: collections, isError } = useGetCollectionsQuery();
  const [addTitles, { isLoading: saving }] = useAddTitlesMutation();

  const add = async (collection: CollectionSummary) => {
    if (saving) {
      return;
    }

    try {
      const result = await addTitles({ collectionId: collection.collectionId, contentIds: pending.contentIds }).unwrap();

      toast(result.added > 0 ? `Added to ${collection.name}` : `Already in ${collection.name}`);
    } catch {
      toast('Error adding track');
    }

    onDone();
  };

  return (
    <Modal title="Add to Collection" onClose={onDone}>
      <p className="modal-note">Adding: {pending.title}</p>

      <div className="playlist-select-list">
        {collections === undefined && !isError && <p className="playlist-empty">Loading...</p>}
        {isError && <p className="playlist-empty">Could not load your collections.</p>}
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

      <button type="button" className="btn-secondary modal-wide" onClick={onDone}>Cancel</button>
    </Modal>
  );
};

const CreateDialog = ({ onDone }: { onDone: (created: boolean) => void }) => {
  const toast = useToast();
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [createCollection, { isLoading: saving }] = useCreateCollectionMutation();

  const create = async (event: React.FormEvent) => {
    event.preventDefault();

    const cleaned = name.trim();

    if (!cleaned || saving) {
      return;
    }

    setError(null);

    try {
      await createCollection(cleaned).unwrap();
      toast('Collection created');
      onDone(true);
    } catch (err) {
      setError(catalogError(err, 'Could not create the collection.'));
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

const CollectionDialogs = () => {
  const { pending, creating } = useAppSelector(state => state.collections);
  const dispatch = useAppDispatch();
  const navigate = useNavigate();

  const created = (done: boolean) => {
    dispatch(createClosed());

    if (done) {
      navigate(COLLECTIONS);
    }
  };

  return (
    <>
      {pending && <AddDialog pending={pending} onDone={() => dispatch(addClosed())} />}
      {creating && <CreateDialog onDone={created} />}
    </>
  );
};

export default CollectionDialogs;
