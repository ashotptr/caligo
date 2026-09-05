import { Link } from 'react-router-dom';
import type { PostSummary } from '../types';

const PostCard = ({ post, views }: { post: PostSummary; views?: number }) => (
  <article className="post-card">
    <h2>
      <Link to={`/posts/${post.id}`}>{post.title}</Link>
    </h2>
    <p className="post-meta">
      {post.authorName} · {new Date(post.publishedDate).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })}
      {views !== undefined && (
        <span className="post-views"> · {views.toLocaleString()} view{views === 1 ? '' : 's'}</span>
      )}
    </p>
    <p className="post-excerpt">{post.excerpt}</p>
    {post.tags.length > 0 && (
      <p className="post-tags">
        {post.tags.map(tag => (
          <span key={tag} className="tag">{tag}</span>
        ))}
      </p>
    )}
  </article>
);

export default PostCard;
