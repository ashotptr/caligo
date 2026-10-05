import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { useAuth } from '../contexts/AuthContext';
import { formatDateTime, parseServerDate } from '../dates';
import type { PagedResult, PostSummary } from '../types';
import TopNowPanel from '../components/TopNowPanel';
import TrendingPanel from '../components/TrendingPanel';

type Tab = 'all' | 'mine';

const DashboardPage = () => {
  const { user, hasRole } = useAuth();
  const isAdmin = hasRole('Admin');

  // Admins see the public feed by default; "My posts" is the only list that includes scheduled posts.
  const [tab, setTab] = useState<Tab>(isAdmin ? 'all' : 'mine');
  const [posts, setPosts] = useState<PostSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Switching tabs quickly must not let the slower, older response replace the newer list.
  const request = useRef(0);

  const scope = useCallback(() => (tab === 'all' ? {} : { mine: true }), [tab]);

  const fetchPosts = useCallback(async () => {
    const current = ++request.current;

    setLoading(true);
    setError(null);
    setPosts([]);
    setCursor(null);
    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', {
        params: scope()
      });
      if (current !== request.current) return;
      setPosts(response.data.items);
      setCursor(response.data.nextCursor);
    } catch {
      if (current !== request.current) return;
      setError('Could not load posts.');
    } finally {
      if (current === request.current) setLoading(false);
    }
  }, [scope]);

  const loadMore = async () => {
    if (!cursor || loadingMore) {
      return;
    }

    const current = request.current;

    setLoadingMore(true);
    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', {
        params: { ...scope(), cursor }
      });
      if (current !== request.current) return;
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
        <h1>{tab === 'all' ? 'All posts' : 'My posts'}</h1>
        {canWrite && <Link to="/posts/new" className="button-link">New post</Link>}
      </div>

      {isAdmin && (
        <div className="tabs" role="group" aria-label="Which posts">
          <button type="button" aria-pressed={tab === 'all'} onClick={() => setTab('all')}>All posts</button>
          <button type="button" aria-pressed={tab === 'mine'} onClick={() => setTab('mine')}>My posts</button>
        </div>
      )}

      <p className="status-line">Signed in as <strong>{user?.name}</strong> ({user?.roles.join(', ') || 'no role'})</p>

      <TrendingPanel />
      {canWrite && <TopNowPanel />}

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
              <span className="post-meta">
                {' · '}
                {post.status === 'scheduled' && post.scheduledFor
                  ? <span className="badge badge-scheduled">Scheduled · {formatDateTime(post.scheduledFor)}</span>
                  : parseServerDate(post.publishedDate).toLocaleDateString()}
                {tab === 'all' ? ` · ${post.authorName}` : ''}
              </span>
            </div>
            {canWrite && (
              <div className="dashboard-actions">
                <Link to={`/posts/${post.id}/edit`}>Edit</Link>
                {post.status !== 'scheduled' && (
                  <Link to={`/posts/${post.id}/stats`} state={{ title: post.title }}>Stats</Link>
                )}
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
