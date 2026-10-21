import { useCallback, useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import PostCard from '../components/PostCard';
import type { PagedResult, PostStats, PostStatsResponse, PostSummary } from '../types';
import SubscribeForm from '../components/SubscribeForm';

const PostsListPage = () => {
  const [posts, setPosts] = useState<PostSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [params] = useSearchParams();
  const activeQuery = (params.get('q') ?? '').trim();
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stats, setStats] = useState<Record<number, PostStats>>({});

  const fetchPosts = useCallback(async (searchTerm: string) => {
    setLoading(true);
    setError(null);
    try {
      if (searchTerm) {
        const response = await api.get<PostSummary[]>('/api/blogposts/search', {
          params: { query: searchTerm }
        });
        setPosts(response.data);
        setCursor(null);
      } else {
        const response = await api.get<PagedResult<PostSummary>>('/api/blogposts');
        setPosts(response.data.items);
        setCursor(response.data.nextCursor);
      }
    } catch (err) {
      console.error('Error loading posts:', err);
      setError('Could not load posts.');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadMore = async () => {
    if (!cursor || loadingMore) {
      return;
    }

    setLoadingMore(true);
    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', {
        params: { cursor }
      });
      setPosts(previous => [...previous, ...response.data.items]);
      setCursor(response.data.nextCursor);
    } catch (err) {
      console.error('Error loading more posts:', err);
      setError('Could not load more posts.');
    } finally {
      setLoadingMore(false);
    }
  };

  useEffect(() => {
    fetchPosts(activeQuery);
  }, [activeQuery, fetchPosts]);

  useEffect(() => {
    if (posts.length === 0) {
      return;
    }

    const ids = posts.map(p => p.id).join(',');

    api
      .get<PostStatsResponse>('/api/analytics/posts', { params: { ids } })
      .then(response => {
        if (!response.data.enabled) {
          return;
        }
        setStats(previous => ({
          ...previous,
          ...Object.fromEntries(response.data.posts.map(s => [s.postId, s]))
        }));
      })
      .catch(() => undefined);
  }, [posts]);

  return (
    <div>
      <h1>{activeQuery ? 'Search' : 'Posts'}</h1>

      {activeQuery && (
        <p className="search-summary">
          Posts matching “{activeQuery}” · <Link to="/">Clear</Link>
        </p>
      )}

      {loading && <p className="status-line">Loading posts…</p>}
      {error && <p className="form-error">{error}</p>}

      {!loading && !error && posts.length === 0 && (
        <p className="status-line">
          {activeQuery ? `No posts match “${activeQuery}”.` : 'No posts yet.'}
        </p>
      )}

      {posts.map(post => (
        <PostCard key={post.id} post={post} views={stats[post.id]?.views} />
      ))}

      {!loading && !activeQuery && posts.length > 0 && <SubscribeForm />}

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

export default PostsListPage;
