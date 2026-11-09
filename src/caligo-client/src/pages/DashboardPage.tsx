import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useDeletePostMutation, useGetPostsInfiniteQuery } from '../api/postsApi';
import TopNowPanel from '../components/TopNowPanel';
import { formatDateTime, parseServerDate } from '../dates';
import { useAuth } from '../store/hooks';

type Tab = 'all' | 'mine';

const DashboardPage = () => {
  const { user, hasRole } = useAuth();
  const isAdmin = hasRole('Admin');

  const [tab, setTab] = useState<Tab>(isAdmin ? 'all' : 'mine');
  const [error, setError] = useState<string | null>(null);

  const list = useGetPostsInfiniteQuery(tab === 'all' ? {} : { mine: true });
  const [deletePost] = useDeletePostMutation();

  const posts = list.currentData?.pages.flatMap(page => page.items) ?? [];
  const loading = list.isFetching && !list.currentData;
  const failed = list.isError && !list.isFetchNextPageError;

  const handleDelete = async (id: number) => {
    if (!window.confirm('Delete this post permanently?')) return;

    setError(null);

    try {
      await deletePost({ id }).unwrap();
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

      {canWrite && <TopNowPanel />}

      {loading && <p className="status-line">Loading…</p>}
      {failed && <p className="form-error">Could not load posts.</p>}
      {list.isFetchNextPageError && <p className="form-error">Could not load more posts.</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !failed && posts.length === 0 && (
        <p className="status-line">No posts yet, write your first one!</p>
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

      {!loading && list.hasNextPage && (
        <div className="load-more">
          <button type="button" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
            {list.isFetchingNextPage ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </div>
  );
};

export default DashboardPage;
