import { useEffect, useState } from 'react';
import { useFindContentQuery } from '../api/catalogApi';
import { kindLabel, statusOf, yearOf } from '../catalog';
import type { ContentSummary } from '../types';

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
  const [term, setTerm] = useState('');

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(query.trim()), 250);

    return () => window.clearTimeout(timer);
  }, [query]);

  const searching = term.length >= 2 && term === query.trim();
  const { currentData, error } = useFindContentQuery({ query: term, limit: 8 }, { skip: !searching });

  const results = searching ? currentData?.items ?? [] : [];
  const searched = searching && currentData !== undefined;
  const unavailable = statusOf(error) === 503;

  const pick = (item: ContentSummary) => {
    onChange({ contentId: item.contentId, title: item.title });
    setQuery('');
    setTerm('');
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
            placeholder="Find a movie, an episode or a song in the Thesaurarium…"
            maxLength={100}
            disabled={unavailable}
            onChange={e => setQuery(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter') e.preventDefault();
            }}
          />

          {unavailable && <p className="hint">The Thesaurarium isn't available right now.</p>}
          {searched && results.length === 0 && <p className="hint">Nothing in the Thesaurarium matches.</p>}

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
