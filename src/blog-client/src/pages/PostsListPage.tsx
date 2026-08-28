import { useCallback, useEffect, useState } from 'react';
import api from '../api/axios';
import PostCard from '../components/PostCard';
import type { PagedResult, PostStats, PostStatsResponse, PostSummary } from '../types';
import SubscribeForm from '../components/SubscribeForm';

const PostsListPage = () => {
  const [posts, setPosts] = useState<PostSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [activeQuery, setActiveQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stats, setStats] = useState<Record<number, PostStats>>({});

  const fetchPosts = useCallback(async (searchTerm: string) => {
    setLoading(true);
    setError(null);
    try {
      if (searchTerm) {
        // Search is relevance-ranked rather than keyset paged, so it returns a plain
        // top-N list and there is no cursor to carry.
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
      setError('Could not load posts. Is the API running?');
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

    // One batched call for the whole page. Asking per post would be twenty round trips to
    // render one screen, which is why the gRPC contract has a batch method at all.
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

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    setActiveQuery(query.trim());
  };

  return (
    <div>
      <form onSubmit={handleSearch} className="search-bar">
        <input
          type="search"
          placeholder="Search posts…"
          value={query}
          onChange={e => setQuery(e.target.value)}
        />
        <button type="submit">Search</button>
        {activeQuery && (
          <button
            type="button"
            className="link-button"
            onClick={() => {
              setQuery('');
              setActiveQuery('');
            }}
          >
            Clear
          </button>
        )}
      </form>

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
