import { formatDuration, formatRating, kindLabel, yearOf } from '../catalog';
import { titlePath } from '../thesaurarium';
import CoverCard from './CoverCard';
import type { ContentSummary } from '../types';

const ContentCard = ({ content }: { content: ContentSummary }) => (
  <CoverCard
    to={titlePath(content.contentId)}
    cover={content.coverUri}
    kind={kindLabel(content.kind)}
    title={content.title}
    sub={[kindLabel(content.kind), yearOf(content.releaseDate)].filter(part => part.length > 0).join(' · ')}
    meta={`${formatDuration(content.duration, content.kind)} · ${formatRating(content.averageRating)}`}
    tags={content.genres}
  />
);

export default ContentCard;
