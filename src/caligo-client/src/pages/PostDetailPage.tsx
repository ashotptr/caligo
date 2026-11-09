import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useGetPostStatsQuery } from '../api/analyticsApi';
import { apiError, isEditConflict, statusOf } from '../api/errors';
import { useDeletePostMutation, useGetPostQuery } from '../api/postsApi';
import ClapButton from '../components/ClapButton';
import Markdown from '../components/Markdown';
import { formatDate, formatDateTime, parseServerDate } from '../dates';
import { useAuth } from '../store/hooks';
import { titlePath } from '../thesaurarium';

const PostDetailPage = () => {
  const { id = '' } = useParams<{ id: string }>();
  const { data: loaded, error } = useGetPostQuery(id);
  const { data: statsResponse } = useGetPostStatsQuery(id);
  const [deletePost] = useDeletePostMutation();
  const [actionError, setActionError] = useState<string | null>(null);
  const { user, hasRole } = useAuth();
  const navigate = useNavigate();

  if (error) {
    return (
      <div>
        <p className="form-error">{statusOf(error) === 404 ? 'This post does not exist.' : 'Could not load the post.'}</p>
        <Link to="/">← Back to all posts</Link>
      </div>
    );
  }

  if (!loaded) {
    return <p className="status-line">Loading…</p>;
  }

  const { post, etag } = loaded;
  const stats = statsResponse?.enabled ? statsResponse : null;
  const canEdit = user && (hasRole('Admin') || user.id === post.authorId);
  const isScheduled = post.status === 'scheduled';

  const handleDelete = async () => {
    if (!window.confirm('Delete this post permanently?')) return;
    setActionError(null);
    try {
      await deletePost({ id: post.id, etag }).unwrap();
      navigate('/');
    } catch (err) {
      setActionError(isEditConflict(err)
        ? 'This post changed since you opened it. Reload the page before deleting it.'
        : apiError(err, 'Could not delete the post.'));
    }
  };

  return (
    <article className="post-detail">
      <Link to="/" className="back-link">← All posts</Link>

      {isScheduled && post.scheduledFor && (
        <p className="notice">
          {parseServerDate(post.scheduledFor) > new Date()
            ? <>Scheduled: goes live on <strong>{formatDateTime(post.scheduledFor)}</strong>. Only you and admins can see it until then.</>
            : <>Going live now. Refresh in a moment.</>}
        </p>
      )}

      <h1>{post.title}</h1>
      <p className="post-meta">
        {post.authorName} · {isScheduled ? 'not published yet' : formatDate(post.publishedDate)}
        {stats && !isScheduled && (
          <span className="post-views" title={`${stats.uniqueVisitors} unique visitor(s)`}>
            {' · '}{stats.views.toLocaleString()} view{stats.views === 1 ? '' : 's'}
          </span>
        )}
        {canEdit && (
          <span className="post-actions">
            <Link to={`/posts/${post.id}/edit`}>Edit</Link>
            {!isScheduled && <Link to={`/posts/${post.id}/stats`} state={{ title: post.title }}>Stats</Link>}
            <button type="button" className="link-button danger" onClick={handleDelete}>Delete</button>
          </span>
        )}
      </p>

      {post.contentId !== null && (
        <p className="review-of">
          Review of <Link to={titlePath(post.contentId)}>{post.contentTitle ?? 'a title in the Thesaurarium'}</Link>
        </p>
      )}

      {actionError && <p className="form-error">{actionError}</p>}

      {post.tags.length > 0 && (
        <p className="post-tags">
          {post.tags.map(tag => (
            <span key={tag} className="tag">{tag}</span>
          ))}
        </p>
      )}

      <div className="post-content">
        <Markdown>{post.content}</Markdown>
      </div>

      {!isScheduled && <ClapButton postId={post.id} authorId={post.authorId} />}
    </article>
  );
};

export default PostDetailPage;
