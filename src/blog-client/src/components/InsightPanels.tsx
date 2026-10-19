import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import type { EngagingContent, PopularContent } from '../types';

const SHOWN = 5;

const InsightPanels = () => {
  const [popular, setPopular] = useState<PopularContent[]>([]);
  const [engaging, setEngaging] = useState<EngagingContent[]>([]);

  useEffect(() => {
    api
      .get<PopularContent[]>('/api/insights/popular')
      .then(response => setPopular(response.data.slice(0, SHOWN)))
      .catch(() => undefined);

    api
      .get<EngagingContent[]>('/api/insights/engaging')
      .then(response => setEngaging(response.data.slice(0, SHOWN)))
      .catch(() => undefined);
  }, []);

  if (popular.length === 0 && engaging.length === 0) {
    return null;
  }

  return (
    <div className="insight-panels">
      {popular.length > 0 && (
        <aside className="trending-panel">
          <h2>Most watched</h2>
          <ol className="trending-list">
            {popular.map(item => (
              <li key={item.contentId}>
                <Link to={`/catalog/${item.contentId}`}>{item.title}</Link>
                <span className="trending-score">{item.totalViews}</span>
              </li>
            ))}
          </ol>
        </aside>
      )}

      {engaging.length > 0 && (
        <aside className="trending-panel">
          <h2>Most engaging</h2>
          <ol className="trending-list">
            {engaging.map(item => (
              <li key={item.contentId}>
                <Link to={`/catalog/${item.contentId}`}>{item.title}</Link>
                <span className="trending-score" title="Ratings and comments per view">
                  {item.engagementRatio == null ? '' : item.engagementRatio.toFixed(2)}
                </span>
              </li>
            ))}
          </ol>
        </aside>
      )}
    </div>
  );
};

export default InsightPanels;
