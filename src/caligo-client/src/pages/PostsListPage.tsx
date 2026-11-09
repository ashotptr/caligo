import { Link, useSearchParams } from 'react-router-dom';
import { useGetPostStatsBatchQuery } from '../api/analyticsApi';
import { useGetPostsInfiniteQuery, useSearchPostsQuery } from '../api/postsApi';
import PostCard from '../components/PostCard';
import SubscribeForm from '../components/SubscribeForm';
import TrendingPanel from '../components/TrendingPanel';
import type { PostStats } from '../types';

const PostsListPage = () => {
  const [params] = useSearchParams();
  const activeQuery = (params.get('q') ?? '').trim();

  const search = useSearchPostsQuery(activeQuery, { skip: !activeQuery });
  const list = useGetPostsInfiniteQuery({}, { skip: !!activeQuery });

  const posts = activeQuery
    ? search.currentData ?? []
    : list.data?.pages.flatMap(page => page.items) ?? [];

  const loading = activeQuery ? search.isFetching && !search.currentData : list.isLoading;
  const failed = activeQuery ? search.isError : list.isError && !list.isFetchNextPageError;

  const ids = posts.map(post => post.id);
  const { data: statsResponse } = useGetPostStatsBatchQuery(ids, { skip: ids.length === 0 });

  const stats: Record<number, PostStats> = statsResponse?.enabled
    ? Object.fromEntries(statsResponse.posts.map(entry => [entry.postId, entry]))
    : {};

  return (
    <div>
      <h1>{activeQuery ? 'Search' : 'Posts'}</h1>

      {activeQuery && (
        <p className="search-summary">
          Posts matching “{activeQuery}” · <Link to="/">Clear</Link>
        </p>
      )}

      {!activeQuery && <TrendingPanel />}

      {loading && <p className="status-line">Loading posts…</p>}
      {failed && <p className="form-error">Could not load posts.</p>}
      {list.isFetchNextPageError && <p className="form-error">Could not load more posts.</p>}

      {!loading && !failed && posts.length === 0 && (
        <p className="status-line">
          {activeQuery ? `No posts match “${activeQuery}”.` : 'No posts yet.'}
        </p>
      )}

      {posts.map(post => (
        <PostCard key={post.id} post={post} views={stats[post.id]?.views} />
      ))}

      {!loading && !activeQuery && posts.length > 0 && <SubscribeForm />}

      {!loading && !activeQuery && list.hasNextPage && (
        <div className="load-more">
          <button type="button" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage}>
            {list.isFetchingNextPage ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </div>
  );
};

export default PostsListPage;
