import { useState } from 'react';
import type { CSSProperties } from 'react';

interface CoverProps {
  src?: string | null;
  title: string;
  kind: string;
  className?: string;
}

const hueOf = (text: string): number => {
  let hue = 0;

  for (const char of text) {
    hue = (hue * 31 + (char.codePointAt(0) ?? 0)) % 360;
  }

  return hue;
};

const initialsOf = (title: string): string => {
  const words = title.replace(/[^\p{L}\p{N}\s]/gu, ' ').split(/\s+/).filter(word => word.length > 0);
  const letters = words.slice(0, 2).map(word => [...word][0]).join('');

  return letters.length > 0 ? letters.toUpperCase() : '·';
};

const Cover = ({ src, title, kind, className = '' }: CoverProps) => {
  const [failed, setFailed] = useState<string | null>(null);

  if (src && failed !== src) {
    return <img className={`cover-image ${className}`} src={src} alt="" loading="lazy" onError={() => setFailed(src)} />;
  }

  return (
    <span
      className={`cover-art ${className}`}
      style={{ '--cover-hue': hueOf(title) } as CSSProperties}
      aria-hidden="true"
    >
      <span className="cover-initials">{initialsOf(title)}</span>
      <span className="cover-kind">{kind}</span>
    </span>
  );
};

export default Cover;
