import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import axios from 'axios';
import api from '../api/axios';
import { apiError, isEditConflict } from '../api/errors';
import { etagOf } from '../api/etag';
import ClapButton from '../components/ClapButton';
import Markdown from '../components/Markdown';
import { useAuth } from '../contexts/AuthContext';
import { formatDate, formatDateTime, parseServerDate } from '../dates';
import { titlePath } from '../thesaurarium';
import type { PostDetail, PostStats } from '../types';

const PostDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const [post, setPost] = useState<PostDetail | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [stats, setStats] = useState<PostStats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const { user, hasRole } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    const fetchPost = async () => {
      try {
        const response = await api.get<PostDetail>(`/api/blogposts/${id}`);
        setPost(response.data);
        setEtag(etagOf(response));
      } catch (err) {
        if (axios.isAxiosError(err) && err.response?.status === 404) {
          setError('This post does not exist.');
        } else {
          setError('Could not load the post.');
        }
      }
    };
    fetchPost();
  }, [id]);

  useEffect(() => {
    const fetchStats = async () => {
      try {
        const response = await api.get<PostStats & { enabled: boolean }>(`/api/analytics/posts/${id}`);
        setStats(response.data.enabled ? response.data : null);
      } catch {
        setStats(null);
      }
    };
    fetchStats();
  }, [id]);

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to="/">← Back to all posts</Link>
      </div>
    );
  }

  if (!post) {
    return <p className="status-line">Loading…</p>;
  }

  const canEdit = user && (hasRole('Admin') || user.id === post.authorId);
  const isScheduled = post.status === 'scheduled';

  const handleDelete = async () => {
    if (!window.confirm('Delete this post permanently?')) return;
    setActionError(null);
    try {
      await api.delete(`/api/blogposts/${post.id}`, { headers: etag ? { 'If-Match': etag } : {} });
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
