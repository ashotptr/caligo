import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAddCommentMutation, useGetCommentsInfiniteQuery } from '../api/socialApi';
import { PROFILE, catalogError, statusOf } from '../catalog';
import { useAuth } from '../store/hooks';

const MAX_LENGTH = 4000;

const CommentsSection = ({ contentId }: { contentId: number }) => {
  const { isAuthenticated } = useAuth();
  const { data, hasNextPage, fetchNextPage, isFetchNextPageError, error: loadError } = useGetCommentsInfiniteQuery(contentId);
  const [addComment, { isLoading: busy }] = useAddCommentMutation();

  const [text, setText] = useState('');
  const [needsProfile, setNeedsProfile] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setNeedsProfile(false);
    setError(null);
  }, [contentId]);

  const comments = data?.pages.flat() ?? [];

  const post = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await addComment({ contentId, comment: text }).unwrap();
      setText('');
    } catch (err) {
      if (statusOf(err) === 409) {
        setNeedsProfile(true);
      } else {
        setError(catalogError(err, 'Could not post the comment.'));
      }
    }
  };

  const shownError = error ?? (isFetchNextPageError ? catalogError(loadError, 'Could not load older comments.') : null);

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
          Comments need a profile. <Link to={PROFILE}>Set yours up</Link> and come back.
        </p>
      )}

      {shownError && <p className="form-error">{shownError}</p>}

      {comments.length === 0 && <p className="status-line">No comments yet.</p>}

      <ul className="comment-list">
        {comments.map(comment => (
          <li key={comment.commentId}>
            <p className="post-meta">{comment.author}</p>
            <p>{comment.comment}</p>
          </li>
        ))}
      </ul>

      {hasNextPage && (
        <div className="load-more">
          <button type="button" onClick={() => void fetchNextPage()}>Older comments</button>
        </div>
      )}
    </section>
  );
};

export default CommentsSection;
