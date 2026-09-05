import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import axios from 'axios';
import api from '../api/axios';
import Markdown from '../components/Markdown';
import { useAuth } from '../contexts/AuthContext';
import type { PostDetail, PostStats } from '../types';

const PostDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const [post, setPost] = useState<PostDetail | null>(null);
  const [stats, setStats] = useState<PostStats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { user, hasRole } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    const fetchPost = async () => {
      try {
        const response = await api.get<PostDetail>(`/api/blogposts/${id}`);
        setPost(response.data);
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

  const handleDelete = async () => {
    if (!window.confirm('Delete this post permanently?')) return;
    try {
      await api.delete(`/api/blogposts/${post.id}`);
      navigate('/');
    } catch {
      setError('Could not delete the post.');
    }
  };

  return (
    <article className="post-detail">
      <Link to="/" className="back-link">← All posts</Link>
      <h1>{post.title}</h1>
      <p className="post-meta">
        {post.authorName} · {new Date(post.publishedDate).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })}
        {stats && (
          <span className="post-views" title={`${stats.uniqueVisitors} unique visitor(s)`}>
            {' · '}{stats.views.toLocaleString()} view{stats.views === 1 ? '' : 's'}
          </span>
        )}
        {canEdit && (
          <span className="post-actions">
            <Link to={`/posts/${post.id}/edit`}>Edit</Link>
            <button type="button" className="link-button danger" onClick={handleDelete}>Delete</button>
          </span>
        )}
      </p>

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
    </article>
  );
};

export default PostDetailPage;
