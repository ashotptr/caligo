import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  useGetInteractionQuery,
  useLogWatchMutation,
  useRemoveInteractionMutation,
  useSaveInteractionMutation
} from '../api/socialApi';
import { PROFILE, catalogError, statusOf, watchVerb } from '../catalog';
import { useAuth } from '../store/hooks';
import type { ContentDetail } from '../types';

interface YourActivityProps {
  content: ContentDetail;
  position?: () => number | null;
}

const YourActivity = ({ content, position }: YourActivityProps) => {
  const { isAuthenticated } = useAuth();
  const contentId = content.contentId;

  const { currentData: interaction, error: loadError } = useGetInteractionQuery(contentId, { skip: !isAuthenticated });
  const [saveInteraction, { isLoading: saving }] = useSaveInteractionMutation();
  const [removeInteraction, { isLoading: removing }] = useRemoveInteractionMutation();
  const [logWatchCall, { isLoading: logging }] = useLogWatchMutation();

  const [rating, setRating] = useState('');
  const [liked, setLiked] = useState<boolean | null>(null);
  const [saved, setSaved] = useState(false);
  const [needsProfile, setNeedsProfile] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const busy = saving || removing || logging;

  useEffect(() => {
    setRating('');
    setLiked(null);
    setSaved(false);
    setNeedsProfile(false);
    setMessage(null);
    setError(null);
  }, [contentId, isAuthenticated]);

  useEffect(() => {
    if (interaction) {
      setRating(interaction.rating == null ? '' : String(interaction.rating));
      setLiked(interaction.liked);
      setSaved(true);
    }
  }, [interaction]);

  useEffect(() => {
    if (statusOf(loadError) === 409) {
      setNeedsProfile(true);
    }
  }, [loadError]);

  if (!isAuthenticated) {
    return (
      <p className="status-line">
        <Link to="/login">Sign in</Link> to rate this, comment on it and keep a history.
      </p>
    );
  }

  if (needsProfile) {
    return (
      <p className="notice">
        Rating, commenting and history need a profile. <Link to={PROFILE}>Set yours up</Link>, it takes a minute.
      </p>
    );
  }

  const fail = (err: unknown, fallback: string) => {
    if (statusOf(err) === 409) {
      setNeedsProfile(true);
    } else {
      setError(catalogError(err, fallback));
    }
  };

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setMessage(null);

    if (rating === '' && liked === null) {
      setError('Pick a rating, a like, or both.');
      return;
    }

    try {
      await saveInteraction({ contentId, liked, rating: rating === '' ? null : Number(rating) }).unwrap();
      setSaved(true);
      setMessage('Saved.');
    } catch (err) {
      fail(err, 'Could not save your rating.');
    }
  };

  const remove = async () => {
    setError(null);
    setMessage(null);
    try {
      await removeInteraction(contentId).unwrap();
      setRating('');
      setLiked(null);
      setSaved(false);
      setMessage('Removed.');
    } catch (err) {
      fail(err, 'Could not remove your rating.');
    }
  };

  const logWatch = async () => {
    setError(null);
    setMessage(null);
    try {
      const seconds = position?.() ?? null;

      await logWatchCall({ contentId, timestamp: seconds && seconds > 0 ? seconds : null }).unwrap();
      setMessage(`Added to your history. Your recommendations now follow this ${watchVerb(content.kind)}.`);
    } catch (err) {
      fail(err, 'Could not add it to your history.');
    }
  };

  return (
    <form onSubmit={save} className="your-activity">
      <label>
        Your rating
        <input
          type="number"
          min={0}
          max={10}
          step={0.5}
          value={rating}
          placeholder="0 – 10"
          onChange={e => setRating(e.target.value)}
        />
      </label>

      <div className="tabs" role="group" aria-label="Like or dislike">
        <button type="button" aria-pressed={liked === true} onClick={() => setLiked(liked === true ? null : true)}>
          Like
        </button>
        <button type="button" aria-pressed={liked === false} onClick={() => setLiked(liked === false ? null : false)}>
          Dislike
        </button>
      </div>

      <button type="submit" disabled={busy}>Save</button>

      {saved && (
        <button type="button" className="link-button danger" onClick={remove} disabled={busy}>
          Remove
        </button>
      )}

      <button type="button" className="link-button" onClick={logWatch} disabled={busy}>
        Log a {watchVerb(content.kind)}
      </button>

      {message && <p className="hint">{message}</p>}
      {error && <p className="form-error">{error}</p>}
    </form>
  );
};

export default YourActivity;
