import { useState } from 'react';
import { Link } from 'react-router-dom';

interface CoverCardProps {
  to: string;
  cover: string | null;
  placeholder: string;
  title: string;
  sub: string;
  meta?: string;
}

const CoverCard = ({ to, cover, placeholder, title, sub, meta }: CoverCardProps) => {
  const [failed, setFailed] = useState<string | null>(null);
  const shown = cover && failed !== cover ? cover : null;

  return (
    <Link to={to} className="card album-card">
      <span className="album-cover">
        {shown ? (
          <img className="album-art" src={shown} alt="" loading="lazy" onError={() => setFailed(shown)} />
        ) : (
          <span className="cover-placeholder" aria-hidden="true">{placeholder}</span>
        )}
      </span>
      <span className="card-title">{title}</span>
      <span className="card-sub">{sub}</span>
      {meta && <span className="card-meta">{meta}</span>}
    </Link>
  );
};

export default CoverCard;
