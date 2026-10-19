import { useEffect, useState } from 'react';
import api from '../api/axios';
import { catalogError } from '../catalog';
import DataTable from '../components/DataTable';
import type {
  ActiveUser,
  EngagingContent,
  MusicImportResult,
  PlaybackQuality,
  PopularContent,
  QueryResult,
  SavedQuery
} from '../types';

const InsightsPage = () => {
  const [popular, setPopular] = useState<PopularContent[]>([]);
  const [engaging, setEngaging] = useState<EngagingContent[]>([]);
  const [activeUsers, setActiveUsers] = useState<ActiveUser[]>([]);
  const [queries, setQueries] = useState<SavedQuery[]>([]);
  const [result, setResult] = useState<QueryResult | null>(null);
  const [running, setRunning] = useState<number | null>(null);
  const [importing, setImporting] = useState(false);
  const [imported, setImported] = useState<MusicImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [playback, setPlayback] = useState<PlaybackQuality | null>(null);

  useEffect(() => {
    let cancelled = false;

    Promise.all([
      api.get<PopularContent[]>('/api/insights/popular'),
      api.get<EngagingContent[]>('/api/insights/engaging'),
      api.get<ActiveUser[]>('/api/insights/active-users'),
      api.get<SavedQuery[]>('/api/insights/queries')
    ])
      .then(([watched, engaged, users, saved]) => {
        if (cancelled) return;
        setPopular(watched.data);
        setEngaging(engaged.data);
        setActiveUsers(users.data);
        setQueries(saved.data);
      })
      .catch(err => {
        if (!cancelled) setError(catalogError(err, 'Could not load the insights.'));
      });

    api.get<PlaybackQuality>('/api/analytics/playback?days=7')
      .then(response => {
        if (!cancelled) setPlayback(response.data);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, []);

  const run = async (number: number) => {
    setError(null);
    setRunning(number);
    try {
      const response = await api.get<QueryResult>(`/api/insights/queries/${number}`);
      setResult(response.data);
    } catch (err) {
      setError(catalogError(err, `Query ${number} failed.`));
    } finally {
      setRunning(null);
    }
  };

  const importMusic = async () => {
    setError(null);
    setImported(null);
    setImporting(true);
    try {
      const response = await api.post<MusicImportResult>('/api/music/import');
      setImported(response.data);
    } catch (err) {
      setError(catalogError(err, 'The import failed.'));
    } finally {
      setImporting(false);
    }
  };

  return (
    <div className="stats-page">
      <h1>Catalog insights</h1>

      {error && <p className="form-error">{error}</p>}

      <section className="stats-section">
        <h2>Most watched</h2>
        <p className="hint">View popularcontent</p>
        <DataTable
          columns={['content_id', 'title', 'totalviews']}
          rows={popular.map(row => [row.contentId, row.title, row.totalViews])}
        />
      </section>

      <section className="stats-section">
        <h2>Most engaging</h2>
        <p className="hint">View engagingcontent</p>
        <DataTable
          columns={['content_id', 'title', 'total_views', 'total_interactions', 'comments_left', 'engagement_ratio']}
          rows={engaging.map(row => [
            row.contentId, row.title, row.totalViews, row.totalInteractions, row.commentsLeft, row.engagementRatio
          ])}
        />
      </section>

      <section className="stats-section">
        <h2>Most active users</h2>
        <p className="hint">View activeusers</p>
        <DataTable
          columns={['user_id', 'full_name', 'total_views', 'total_interactions', 'comments_left']}
          rows={activeUsers.map(row => [
            row.userId, row.fullName, row.totalViews, row.totalInteractions, row.commentsLeft
          ])}
        />
      </section>

      <section className="stats-section">
        <h2>Saved queries</h2>
        <ol className="query-list">
          {queries.map(query => (
            <li key={query.number}>
              <span>{query.title}</span>
              <button
                type="button"
                className="link-button"
                onClick={() => run(query.number)}
                disabled={running !== null}
              >
                {running === query.number ? 'Running…' : 'Run'}
              </button>
            </li>
          ))}
        </ol>

        {result && (
          <>
            <p className="status-line">
              Query {result.number}: {result.title} · {result.rows.length} row{result.rows.length === 1 ? '' : 's'}
            </p>
            <DataTable columns={result.columns} rows={result.rows} />
          </>
        )}
      </section>

      <section className="stats-section">
        <h2>Video playback</h2>
        <p className="hint">
          Last {playback?.days ?? 7} days of player events (start-up, stalls, quality switches), from Kafka through ClickHouse.
        </p>
        {playback && !playback.enabled && <p className="status-line">Playback analytics are switched off on this server.</p>}
        {playback?.enabled && playback.titles.length === 0 && <p className="status-line">Nobody has watched anything yet.</p>}
        {playback?.enabled && playback.titles.length > 0 && (
          <DataTable
            columns={['title', 'sessions', 'median start-up ms', 'p95 start-up ms', 'stalls', 'stalled s', 'quality switches', 'errors', 'hls.js / native / dash / mp4']}
            rows={playback.titles.map(row => [
              row.title,
              row.sessions,
              Math.round(row.medianStartupMs),
              Math.round(row.p95StartupMs),
              row.stalls,
              Math.round(row.stallMs / 100) / 10,
              row.qualitySwitches,
              row.errors,
              `${row.hlsStartups} / ${row.nativeHlsStartups} / ${row.dashStartups} / ${row.progressiveStartups}`
            ])}
          />
        )}
      </section>

      <section className="stats-section">
        <h2>Music library</h2>
        <p className="lede">
          Reads the radio playlist and the mixtape albums from Caligo's files and adds what is missing as
          artists, albums and songs. Running it again adds nothing twice.
        </p>
        <button type="button" onClick={importMusic} disabled={importing}>
          {importing ? 'Importing…' : 'Import music'}
        </button>

        {imported && (
          <div className="notice">
            <p>
              {imported.releases} release{imported.releases === 1 ? '' : 's'} and {imported.tracks} track{imported.tracks === 1 ? '' : 's'} read.
              Added {imported.artistsAdded} artist{imported.artistsAdded === 1 ? '' : 's'}, {imported.albumsAdded} album{imported.albumsAdded === 1 ? '' : 's'}, {imported.singlesAdded} single{imported.singlesAdded === 1 ? '' : 's'} and {imported.songsAdded} song{imported.songsAdded === 1 ? '' : 's'};
              {' '}{imported.songsAlreadyThere} song{imported.songsAlreadyThere === 1 ? ' was' : 's were'} already there.
            </p>
            {imported.problems.length > 0 && (
              <ul className="form-error">
                {imported.problems.map(problem => (
                  <li key={problem}>{problem}</li>
                ))}
              </ul>
            )}
          </div>
        )}
      </section>
    </div>
  );
};

export default InsightsPage;
