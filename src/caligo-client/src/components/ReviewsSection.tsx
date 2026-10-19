import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { useAuth } from '../contexts/AuthContext';
import { formatDate } from '../dates';
import type { PagedResult, PostSummary } from '../types';

const ReviewsSection = ({ contentId }: { contentId: number }) => {
  const { hasRole } = useAuth();

  const [reviews, setReviews] = useState<PostSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setReviews([]);
    setCursor(null);

    api
      .get<PagedResult<PostSummary>>('/api/blogposts', { params: { contentId } })
      .then(response => {
        if (cancelled) return;
        setReviews(response.data.items);
        setCursor(response.data.nextCursor);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [contentId]);

  const loadMore = async () => {
    if (!cursor) {
      return;
    }

    try {
      const response = await api.get<PagedResult<PostSummary>>('/api/blogposts', { params: { contentId, cursor } });
      setReviews(previous => [...previous, ...response.data.items]);
      setCursor(response.data.nextCursor);
    } catch {
      setCursor(null);
    }
  };

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

      {cursor && (
        <div className="load-more">
          <button type="button" onClick={loadMore}>More reviews</button>
        </div>
      )}
    </section>
  );
};

export default ReviewsSection;
