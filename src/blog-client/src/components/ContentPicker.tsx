import { useEffect, useState } from 'react';
import api from '../api/axios';
import { kindLabel, statusOf, yearOf } from '../catalog';
import type { ContentSummary, PagedResult } from '../types';

export interface PickedContent {
  contentId: number;
  title: string;
}

interface ContentPickerProps {
  value: PickedContent | null;
  onChange: (value: PickedContent | null) => void;
}

const ContentPicker = ({ value, onChange }: ContentPickerProps) => {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<ContentSummary[]>([]);
  const [searched, setSearched] = useState(false);
  const [unavailable, setUnavailable] = useState(false);

  useEffect(() => {
    const term = query.trim();

    setSearched(false);

    if (term.length < 2) {
      setResults([]);
      return;
    }

    let cancelled = false;

    const timer = window.setTimeout(() => {
      api
        .get<PagedResult<ContentSummary>>('/api/content', { params: { query: term, limit: 8 } })
        .then(response => {
          if (cancelled) return;
          setResults(response.data.items);
          setSearched(true);
        })
        .catch(err => {
          if (!cancelled && statusOf(err) === 503) setUnavailable(true);
        });
    }, 250);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [query]);

  const pick = (item: ContentSummary) => {
    onChange({ contentId: item.contentId, title: item.title });
    setQuery('');
    setResults([]);
  };

  return (
    <fieldset className="schedule-field">
      <legend>Review of <span className="hint">(optional)</span></legend>

      {value ? (
        <p className="picked-content">
          <strong>{value.title}</strong>
          <button type="button" className="link-button" onClick={() => onChange(null)}>Remove</button>
        </p>
      ) : (
        <>
          <input
            type="search"
            value={query}
            placeholder="Find a movie, an episode or a song in the catalog…"
            maxLength={100}
            disabled={unavailable}
            onChange={e => setQuery(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter') e.preventDefault();
            }}
          />

          {unavailable && <p className="hint">The catalog is switched off on this server.</p>}
          {searched && results.length === 0 && <p className="hint">Nothing in the catalog matches.</p>}

          {results.length > 0 && (
            <ul className="picker-results">
              {results.map(item => (
                <li key={item.contentId}>
                  <button type="button" className="link-button" onClick={() => pick(item)}>{item.title}</button>
                  <span className="post-meta"> · {kindLabel(item.kind)} · {yearOf(item.releaseDate)}</span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </fieldset>
  );
};

export default ContentPicker;
