import { useState } from 'react';

interface ArtworkProps {
  src: string | null | undefined;
  className: string;
  placeholder?: string;
}

const Artwork = ({ src, className, placeholder }: ArtworkProps) => {
  const [failed, setFailed] = useState<string | null>(null);

  if (src && failed !== src) {
    return <img className={className} src={src} alt="" loading="lazy" onError={() => setFailed(src)} />;
  }

  return placeholder ? <span className={`${className} artwork-missing`} aria-hidden="true">{placeholder}</span> : null;
};

export default Artwork;
