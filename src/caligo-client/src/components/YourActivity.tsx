import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, statusOf, watchVerb } from '../catalog';
import { useAuth } from '../contexts/AuthContext';
import type { ContentDetail, Interaction } from '../types';

interface YourActivityProps {
  content: ContentDetail;
  onChanged: () => void;
  position?: () => number | null;
}

const YourActivity = ({ content, onChanged, position }: YourActivityProps) => {
  const { isAuthenticated } = useAuth();
  const contentId = content.contentId;

  const [rating, setRating] = useState('');
  const [liked, setLiked] = useState<boolean | null>(null);
  const [saved, setSaved] = useState(false);
  const [needsProfile, setNeedsProfile] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setRating('');
    setLiked(null);
    setSaved(false);
    setNeedsProfile(false);
    setMessage(null);
    setError(null);

    if (!isAuthenticated) {
      return;
    }

    let cancelled = false;

    api
      .get<Interaction>(`/api/content/${contentId}/interaction`)
      .then(response => {
        if (cancelled) return;
        setRating(response.data.rating == null ? '' : String(response.data.rating));
        setLiked(response.data.liked);
        setSaved(true);
      })
      .catch(err => {
        if (!cancelled && statusOf(err) === 409) {
          setNeedsProfile(true);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [contentId, isAuthenticated]);

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
        Rating, commenting and history need a streaming profile. <Link to="/streaming">Create yours</Link>, it takes a minute.
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

    setBusy(true);
    try {
      await api.put<Interaction>(`/api/content/${contentId}/interaction`, {
        liked,
        rating: rating === '' ? null : Number(rating)
      });
      setSaved(true);
      setMessage('Saved.');
      onChanged();
    } catch (err) {
      fail(err, 'Could not save your rating.');
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setError(null);
    setMessage(null);
    setBusy(true);
    try {
      await api.delete(`/api/content/${contentId}/interaction`);
      setRating('');
      setLiked(null);
      setSaved(false);
      setMessage('Removed.');
      onChanged();
    } catch (err) {
      fail(err, 'Could not remove your rating.');
    } finally {
      setBusy(false);
    }
  };

  const logWatch = async () => {
    setError(null);
    setMessage(null);
    setBusy(true);
    try {
      const seconds = position?.() ?? null;

      await api.post(`/api/content/${contentId}/watch`, { timestamp: seconds && seconds > 0 ? seconds : null });
      setMessage(`Added to your history. Your recommendations now follow this ${watchVerb(content.kind)}.`);
      onChanged();
    } catch (err) {
      fail(err, 'Could not add it to your history.');
    } finally {
      setBusy(false);
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
