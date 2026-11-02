import { Link } from 'react-router-dom';
import Cover from './Cover';

interface CoverCardProps {
  to: string;
  cover: string | null | undefined;
  kind: string;
  title: string;
  sub: string;
  meta?: string;
  tags?: string[];
}

const CoverCard = ({ to, cover, kind, title, sub, meta, tags }: CoverCardProps) => (
  <Link to={to} className="card album-card">
    <span className="album-cover">
      <Cover src={cover} title={title} kind={kind} className="album-art" />
    </span>
    <span className="card-title">{title}</span>
    <span className="card-sub">{sub}</span>
    {meta && <span className="card-meta">{meta}</span>}
    {tags && tags.length > 0 && <span className="card-tags">{tags.join(' · ')}</span>}
  </Link>
);

export default CoverCard;
