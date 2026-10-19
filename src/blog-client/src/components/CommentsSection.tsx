import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, statusOf } from '../catalog';
import { useAuth } from '../contexts/AuthContext';
import type { ContentComment } from '../types';

const PAGE = 20;

const MAX_LENGTH = 4000;

const CommentsSection = ({ contentId, onPosted }: { contentId: number; onPosted: () => void }) => {
  const { isAuthenticated } = useAuth();

  const [comments, setComments] = useState<ContentComment[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [text, setText] = useState('');
  const [needsProfile, setNeedsProfile] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setComments([]);
    setHasMore(false);
    setNeedsProfile(false);
    setError(null);

    api
      .get<ContentComment[]>(`/api/content/${contentId}/comments`, { params: { limit: PAGE } })
      .then(response => {
        if (cancelled) return;
        setComments(response.data);
        setHasMore(response.data.length === PAGE);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [contentId]);

  const loadOlder = async () => {
    const last = comments[comments.length - 1];

    if (!last) {
      return;
    }

    try {
      const response = await api.get<ContentComment[]>(`/api/content/${contentId}/comments`, {
        params: { limit: PAGE, before: last.commentId }
      });
      setComments(previous => [...previous, ...response.data]);
      setHasMore(response.data.length === PAGE);
    } catch (err) {
      setError(catalogError(err, 'Could not load older comments.'));
    }
  };

  const post = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const response = await api.post<ContentComment>(`/api/content/${contentId}/comments`, { comment: text });
      setComments(previous => [response.data, ...previous]);
      setText('');
      onPosted();
    } catch (err) {
      if (statusOf(err) === 409) {
        setNeedsProfile(true);
      } else {
        setError(catalogError(err, 'Could not post the comment.'));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="catalog-section">
      <h2>Comments</h2>

      {isAuthenticated && !needsProfile && (
        <form onSubmit={post} className="comment-form">
          <textarea
            value={text}
            onChange={e => setText(e.target.value)}
            rows={3}
            maxLength={MAX_LENGTH}
            placeholder="Say something about it…"
            required
          />
          <button type="submit" disabled={busy || text.trim().length === 0}>
            {busy ? 'Posting…' : 'Post'}
          </button>
        </form>
      )}

      {needsProfile && (
        <p className="notice">
          Comments need a streaming profile. <Link to="/streaming">Create yours</Link> and come back.
        </p>
      )}

      {error && <p className="form-error">{error}</p>}

      {comments.length === 0 && <p className="status-line">No comments yet.</p>}

      <ul className="comment-list">
        {comments.map(comment => (
          <li key={comment.commentId}>
            <p className="post-meta">{comment.author}</p>
            <p>{comment.comment}</p>
          </li>
        ))}
      </ul>

      {hasMore && (
        <div className="load-more">
          <button type="button" onClick={loadOlder}>Older comments</button>
        </div>
      )}
    </section>
  );
};

export default CommentsSection;
