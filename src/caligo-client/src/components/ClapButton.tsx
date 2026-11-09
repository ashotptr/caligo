import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiError } from '../api/errors';
import { postsApi, useClapPostMutation, useGetClapsQuery } from '../api/postsApi';
import { useAppDispatch, useAuth } from '../store/hooks';

const MAX_CLAPS = 50;

const SEND_AFTER_MS = 600;

interface ClapButtonProps {
  postId: number;
  authorId: string | null;
}

const ClapButton = ({ postId, authorId }: ClapButtonProps) => {
  const { user, isAuthenticated } = useAuth();
  const dispatch = useAppDispatch();
  const { data: tally, isError } = useGetClapsQuery(postId);
  const [clapPost] = useClapPostMutation();
  const [error, setError] = useState<string | null>(null);

  const pending = useRef(0);
  const timer = useRef<number | undefined>(undefined);

  const send = useCallback(async () => {
    const count = pending.current;

    if (count === 0) {
      return;
    }

    pending.current = 0;

    try {
      await clapPost({ postId, count }).unwrap();
    } catch (err) {
      setError(apiError(err, 'Your claps could not be sent.'));
    }
  }, [postId, clapPost]);

  useEffect(() => () => {
    window.clearTimeout(timer.current);
    void send();
  }, [send]);

  if (!tally || isError) {
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

    dispatch(postsApi.util.updateQueryData('getClaps', postId, current => {
      current.total += 1;
      current.mine += 1;
    }));

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
