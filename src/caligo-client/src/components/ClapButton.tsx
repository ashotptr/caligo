import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { apiError } from '../api/errors';
import { useAuth } from '../contexts/AuthContext';
import type { ClapResult, ClapTally } from '../types';

const MAX_CLAPS = 50;

const SEND_AFTER_MS = 600;

interface ClapButtonProps {
  postId: number;
  authorId: string | null;
}

const ClapButton = ({ postId, authorId }: ClapButtonProps) => {
  const { user, isAuthenticated } = useAuth();
  const [tally, setTally] = useState<ClapTally | null>(null);
  const [error, setError] = useState<string | null>(null);

  const pending = useRef(0);
  const inFlight = useRef(0);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    let active = true;

    api.get<ClapTally>(`/api/blogposts/${postId}/claps`)
      .then(response => {
        if (active) setTally(response.data);
      })
      .catch(() => {
        if (active) setTally(null);
      });

    return () => {
      active = false;
    };
  }, [postId]);

  const send = useCallback(async () => {
    const count = pending.current;

    if (count === 0) {
      return;
    }

    pending.current = 0;
    inFlight.current += count;

    try {
      const response = await api.post<ClapResult>(`/api/blogposts/${postId}/claps`, { count });

      inFlight.current -= count;

      const refused = count - response.data.applied;

      setTally(current => current && {
        ...current,
        total: current.total - refused,
        mine: response.data.mine + inFlight.current + pending.current
      });
    } catch (err) {
      inFlight.current -= count;

      setTally(current => current && { ...current, total: current.total - count, mine: current.mine - count });
      setError(apiError(err, 'Your claps could not be sent.'));
    }
  }, [postId]);

  useEffect(() => () => {
    window.clearTimeout(timer.current);
    void send();
  }, [send]);

  if (!tally) {
    return null;
  }

  const isAuthor = !!user && user.id === authorId;
  const canClap = isAuthenticated && !isAuthor;
  const capped = tally.mine >= MAX_CLAPS;

  const clap = () => {
    if (capped) {
      return;
    }

    setError(null);
    pending.current += 1;
    setTally(current => current && { ...current, total: current.total + 1, mine: current.mine + 1 });

    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => void send(), SEND_AFTER_MS);
  };

  return (
    <div className="claps">
      {canClap && (
        <button
          type="button"
          className="clap-button"
          onClick={clap}
          disabled={capped}
          title={capped ? `You've given this post all ${MAX_CLAPS} claps` : 'Clap for this post'}
        >
          Clap
        </button>
      )}

      <span className="clap-count">
        {tally.total.toLocaleString()} clap{tally.total === 1 ? '' : 's'}
      </span>

      {canClap && <span className="hint">yours: {tally.mine} / {MAX_CLAPS}</span>}
      {!isAuthenticated && <Link to="/login">Sign in to clap</Link>}
      {error && <span className="form-error">{error}</span>}
    </div>
  );
};

export default ClapButton;
