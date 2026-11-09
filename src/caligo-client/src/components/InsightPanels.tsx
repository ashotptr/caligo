import { Link } from 'react-router-dom';
import { useGetEngagingQuery, useGetPopularQuery } from '../api/insightsApi';
import { titlePath } from '../thesaurarium';

const SHOWN = 5;

const InsightPanels = () => {
  const { data: allPopular = [] } = useGetPopularQuery();
  const { data: allEngaging = [] } = useGetEngagingQuery();

  const popular = allPopular.slice(0, SHOWN);
  const engaging = allEngaging.slice(0, SHOWN);

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
                <Link to={titlePath(item.contentId)}>{item.title}</Link>
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
                <Link to={titlePath(item.contentId)}>{item.title}</Link>
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
