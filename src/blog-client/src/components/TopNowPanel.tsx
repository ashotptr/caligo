import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import type { TopWindowResponse, WindowPost } from '../types';

const WINDOW_MINUTES = 60;
const REFRESH_MS = 30_000;

const TopNowPanel = () => {
  const [by, setBy] = useState<'views' | 'claps'>('views');
  const [top, setTop] = useState<TopWindowResponse | null>(null);

  useEffect(() => {
    let active = true;

    const load = async () => {
      try {
        const response = await api.get<TopWindowResponse>('/api/analytics/top/window', {
          params: { minutes: WINDOW_MINUTES, by, limit: 5 }
        });
        if (active) setTop(response.data);
      } catch {
        if (active) setTop(null);
      }
    };

    load();
    const timer = window.setInterval(load, REFRESH_MS);

    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [by]);

  if (!top?.enabled) {
    return null;
  }

  const score = (post: WindowPost) => (top.by === 'claps' ? post.claps : post.views);
  const ranked = top.posts.filter(post => score(post) > 0);

  return (
    <aside className="trending-panel top-now-panel">
      <h2>Top in the last hour</h2>
      <p className="trending-window">
        by{' '}
        <button type="button" className="link-button" aria-pressed={by === 'views'} onClick={() => setBy('views')}>
          views
        </button>
        {' · '}
        <button type="button" className="link-button" aria-pressed={by === 'claps'} onClick={() => setBy('claps')}>
          claps
        </button>
      </p>

      {ranked.length === 0 ? (
        <p className="status-line">No {top.by} in the last hour yet.</p>
      ) : (
        <ol className="trending-list">
          {ranked.map(post => (
            <li key={post.postId}>
              <Link to={`/posts/${post.postId}/stats`} state={{ title: post.title }}>{post.title}</Link>
              <span className="trending-score">{score(post).toLocaleString()}</span>
            </li>
          ))}
        </ol>
      )}
    </aside>
  );
};

export default TopNowPanel;
