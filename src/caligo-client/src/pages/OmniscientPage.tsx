import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import api from '../api/axios';
import type { AskAnswer, AskSong } from '../types';

type Entry =
  | { id: number; from: 'you'; text: string }
  | { id: number; from: 'ai'; state: 'thinking' }
  | { id: number; from: 'ai'; state: 'failed' }
  | { id: number; from: 'ai'; state: 'answered'; text: string | null; songs: AskSong[] | null };

const SESSION_KEY = 'music_agent_session';

const sessionId = (): string => {
  try {
    const stored = sessionStorage.getItem(SESSION_KEY);

    if (stored) {
      return stored;
    }
  } catch {
    return `sess_${Date.now()}_${Math.random().toString(36).slice(2, 11)}`;
  }

  const created = `sess_${Date.now()}_${Math.random().toString(36).slice(2, 11)}`;

  try {
    sessionStorage.setItem(SESSION_KEY, created);
  } catch {
    return created;
  }

  return created;
};

let history: Entry[] = [];
let nextId = 1;
const listeners = new Set<() => void>();

const publish = (next: Entry[]): void => {
  history = next;
  listeners.forEach(listener => listener());
};

const subscribe = (listener: () => void): (() => void) => {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
};

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

const ask = async (query: string): Promise<void> => {
  const pending = nextId + 1;

  publish([...history, { id: nextId, from: 'you', text: query }, { id: pending, from: 'ai', state: 'thinking' }]);
  nextId += 2;

  let answer: Entry;

  try {
    const { data } = await api.post<AskAnswer>('/api/caligo/ask', { query, sessionId: sessionId() });

    answer = { id: pending, from: 'ai', state: 'answered', text: data.text, songs: data.songs };
  } catch {
    answer = { id: pending, from: 'ai', state: 'failed' };
  }

  publish(history.map(entry => (entry.id === pending ? answer : entry)));
};

const Answer = ({ entry }: { entry: Extract<Entry, { from: 'ai' }> }) => {
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
  const entries = useSyncExternalStore(subscribe, () => history, () => history);
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
    ask(cleaned);
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
