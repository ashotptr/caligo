import type { ReactNode } from 'react';
import Cover from './Cover';

interface DetailHeaderProps {
  cover: string | null | undefined;
  title: string;
  kind: string;
  heading?: ReactNode;
  children?: ReactNode;
}

const DetailHeader = ({ cover, title, kind, heading, children }: DetailHeaderProps) => (
  <div className="portrait-header">
    <span className="release-cover">
      <Cover src={cover} title={title} kind={kind} />
    </span>
    <div className="portrait-text">
      {heading ?? <h1>{title}</h1>}
      {children}
    </div>
  </div>
);

export default DetailHeader;
