import { Link } from 'react-router-dom';
import { formatDuration, formatRating, kindLabel, yearOf } from '../catalog';
import type { ContentSummary } from '../types';

const ContentCard = ({ content }: { content: ContentSummary }) => (
  <Link to={`/catalog/${content.contentId}`} className="card content-card">
    <span className="card-kind">{kindLabel(content.kind)} · {yearOf(content.releaseDate)}</span>
    <span className="card-title">{content.title}</span>
    <span className="card-sub">
      {formatDuration(content.duration, content.kind)} · {formatRating(content.averageRating)}
    </span>
    {content.genres.length > 0 && <span className="card-tags">{content.genres.join(' · ')}</span>}
  </Link>
);

export default ContentCard;
