import { useEffect, useRef, useState } from 'react';
import { useAppDispatch, useAppSelector } from '../store/hooks';
import { askOmniscient } from '../store/omniscientSlice';
import type { OmniscientEntry } from '../store/omniscientSlice';

const safeUrl = (url: string | null): string | null => {
  if (!url) {
    return null;
  }

  try {
    const parsed = new URL(url, window.location.origin);

    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : null;
  } catch {
    return null;
  }
};

const Answer = ({ entry }: { entry: Extract<OmniscientEntry, { from: 'ai' }> }) => {
  if (entry.state === 'thinking') {
    return <div className="ai-thinking">Thinking...</div>;
  }

  if (entry.state === 'failed') {
    return <span className="ai-failed">Connection Failed</span>;
  }

  return (
    <>
      <div className="ai-label">OMNISCIENT AI</div>
      {entry.songs && entry.songs.length > 0 ? (
        <div className="ai-songs">
          {entry.songs.map((song, index) => {
            const href = safeUrl(song.url);

            return (
              <div key={`${song.artist}-${song.title}-${index}`} className="ai-song">
                <div className="ai-song-names">
                  <span className="ai-song-artist">{song.artist}</span>
                  <span className="ai-song-title">{song.title}</span>
                </div>
                {href && (
                  <a href={href} target="_blank" rel="noopener noreferrer" className="ai-open">OPEN ↗</a>
                )}
              </div>
            );
          })}
        </div>
      ) : (
        <div className="ai-text">{entry.text ?? ''}</div>
      )}
    </>
  );
};

const OmniscientPage = () => {
  const entries = useAppSelector(state => state.omniscient.entries);
  const dispatch = useAppDispatch();
  const [query, setQuery] = useState('');
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const timer = window.setTimeout(() => input.current?.focus(), 100);

    return () => window.clearTimeout(timer);
  }, []);

  useEffect(() => {
    if (box.current) {
      box.current.scrollTop = box.current.scrollHeight;
    }
  }, [entries]);

  const send = () => {
    const cleaned = query.trim();

    if (!cleaned) {
      return;
    }

    setQuery('');
    void dispatch(askOmniscient(cleaned));
  };

  return (
    <div className="ai-view">
      <h1>Omniscient AI</h1>

      <div className="ai-history" ref={box}>
        {entries.length === 0 && <div className="ai-empty">Ask me about music, artists, or lyrics...</div>}
        {entries.map(entry => (
          <div key={entry.id} className={entry.from === 'you' ? 'ai-bubble ai-you' : 'ai-bubble ai-agent'}>
            {entry.from === 'you' ? (
              <>
                <div className="ai-you-label">You</div>
                {entry.text}
              </>
            ) : (
              <Answer entry={entry} />
            )}
          </div>
        ))}
      </div>

      <div className="input-container">
        <input
          ref={input}
          type="text"
          className="caligo-input"
          placeholder="Ask the Omniscient Agent..."
          autoComplete="off"
          maxLength={500}
          value={query}
          aria-label="Ask the Omniscient Agent"
          onChange={event => setQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'Enter') send();
          }}
        />
        <button type="button" className="acquire-btn" aria-label="Send" onClick={send}>
          <span className="send-arrow">➤</span>
        </button>
      </div>
    </div>
  );
};

export default OmniscientPage;
