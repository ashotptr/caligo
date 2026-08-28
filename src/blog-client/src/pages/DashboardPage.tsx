import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { useAuth } from '../contexts/AuthContext';
import type { PagedResult, PostSummary } from '../types';
import TrendingPanel from '../components/TrendingPanel';

const DashboardPage = () => {
  const [posts, setPosts] = useState<PostSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { user, hasRole } = useAuth();

  const isAdmin = hasRole('Admin');

  // Admins manage every post; everyone else manages their own. The filter is applied by the
  // API rather than in the browser, because a page of results is no longer the whole list.
  const scope = useCallback(() => (isAdmin ? {} : { mine: true }), [isAdmin]);

  const fetchPosts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', {
        params: scope()
      });
      setPosts(response.data.items);
      setCursor(response.data.nextCursor);
    } catch {
      setError('Could not load posts.');
    } finally {
      setLoading(false);
    }
  }, [scope]);

  const loadMore = async () => {
    if (!cursor || loadingMore) {
      return;
    }

    setLoadingMore(true);
    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', {
        params: { ...scope(), cursor }
      });
      setPosts(previous => [...previous, ...response.data.items]);
      setCursor(response.data.nextCursor);
    } catch {
      setError('Could not load more posts.');
    } finally {
      setLoadingMore(false);
    }
  };

  useEffect(() => {
    fetchPosts();
  }, [fetchPosts]);

  const handleDelete = async (id: number) => {
    if (!window.confirm('Delete this post permanently?')) return;
    try {
      await api.delete(`/api/blogposts/${id}`);
      setPosts(prev => prev.filter(post => post.id !== id));
    } catch {
      setError('Could not delete the post.');
    }
  };

  const canWrite = hasRole('Admin', 'Writer');

  return (
    <div>
      <div className="dashboard-header">
        <h1>{isAdmin ? 'All posts' : 'My posts'}</h1>
        {canWrite && <Link to="/posts/new" className="button-link">New post</Link>}
      </div>

      <p className="status-line">Signed in as <strong>{user?.name}</strong> ({user?.roles.join(', ') || 'no role'})</p>

      <TrendingPanel />

      {loading && <p className="status-line">Loading…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && posts.length === 0 && (
        <p className="status-line">
          {canWrite ? 'No posts yet, write your first one!' : 'Nothing to manage, your account has read access.'}
        </p>
      )}

      <ul className="dashboard-list">
        {posts.map(post => (
          <li key={post.id}>
            <div>
              <Link to={`/posts/${post.id}`}>{post.title}</Link>
              <span className="post-meta"> · {new Date(post.publishedDate).toLocaleDateString()}{isAdmin ? ` · ${post.authorName}` : ''}</span>
            </div>
            {(canWrite || isAdmin) && (
              <div className="dashboard-actions">
                <Link to={`/posts/${post.id}/edit`}>Edit</Link>
                <button type="button" className="link-button danger" onClick={() => handleDelete(post.id)}>
                  Delete
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>

      {!loading && cursor && (
        <div className="load-more">
          <button type="button" onClick={loadMore} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </div>
  );
};

export default DashboardPage;
