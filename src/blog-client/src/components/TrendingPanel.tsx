import { Link } from 'react-router-dom';
import { useTrending } from '../hooks/useTrending';

const TrendingPanel = () => {
  const { snapshot, connected } = useTrending(24, 5);

  if (!connected || !snapshot || snapshot.posts.length === 0) {
    return null;
  }

  return (
    <aside className="trending-panel">
      <h2>
        Trending
        <span className="trending-live" title="Pushed live from the analytics service">live</span>
      </h2>
      <p className="trending-window">last {snapshot.windowHours}h</p>

      <ol className="trending-list">
        {snapshot.posts.map(post => (
          <li key={post.postId}>
            <Link to={`/posts/${post.postId}`}>{post.title}</Link>
            <span className="trending-score">{post.score}</span>
          </li>
        ))}
      </ol>
    </aside>
  );
};

export default TrendingPanel;
