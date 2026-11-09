import { soundWords } from '../ml';
import type { SongAnalysis } from '../types';

const SongSound = ({ song }: { song: SongAnalysis }) => {
  const words = soundWords(song);

  if (words.length === 0) {
    return null;
  }

  return (
    <span className="sound-tags">
      {words.map(word => <span key={word} className="tag">{word}</span>)}
    </span>
  );
};

export default SongSound;
