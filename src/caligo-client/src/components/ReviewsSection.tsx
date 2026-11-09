import { Link } from 'react-router-dom';
import { useGetPostsInfiniteQuery } from '../api/postsApi';
import { formatDate } from '../dates';
import { useAuth } from '../store/hooks';

const ReviewsSection = ({ contentId }: { contentId: number }) => {
  const { hasRole } = useAuth();
  const { data, hasNextPage, fetchNextPage, isFetchNextPageError } = useGetPostsInfiniteQuery({ contentId });

  const reviews = data?.pages.flatMap(page => page.items) ?? [];
  const canWrite = hasRole('Admin', 'Writer');

  return (
    <section className="catalog-section">
      <div className="stats-section-header">
        <h2>Reviews</h2>
        {canWrite && <Link to={`/posts/new?contentId=${contentId}`}>Write a review</Link>}
      </div>

      {reviews.length === 0 && <p className="status-line">Nobody has reviewed this yet.</p>}

      <ul className="review-list">
        {reviews.map(review => (
          <li key={review.id}>
            <Link to={`/posts/${review.id}`}>{review.title}</Link>
            <span className="post-meta"> · {review.authorName} · {formatDate(review.publishedDate)}</span>
            <p className="post-excerpt">{review.excerpt}</p>
          </li>
        ))}
      </ul>

      {hasNextPage && !isFetchNextPageError && (
        <div className="load-more">
          <button type="button" onClick={() => void fetchNextPage()}>More reviews</button>
        </div>
      )}
    </section>
  );
};

export default ReviewsSection;
