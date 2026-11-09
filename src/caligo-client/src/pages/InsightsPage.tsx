import { useState } from 'react';
import { useGetPlaybackQualityQuery } from '../api/analyticsApi';
import { useImportMusicMutation } from '../api/catalogApi';
import {
  useGetActiveUsersQuery,
  useGetEngagingQuery,
  useGetPopularQuery,
  useGetSavedQueriesQuery,
  useLazyRunSavedQueryQuery
} from '../api/insightsApi';
import { catalogError } from '../catalog';
import DataTable from '../components/DataTable';
import MlPanels from '../components/MlPanels';
import ModerationQueue from '../components/ModerationQueue';
import type { MusicImportResult, QueryResult } from '../types';

const InsightsPage = () => {
  const { data: popular = [], error: popularError } = useGetPopularQuery();
  const { data: engaging = [], error: engagingError } = useGetEngagingQuery();
  const { data: activeUsers = [], error: usersError } = useGetActiveUsersQuery();
  const { data: queries = [], error: queriesError } = useGetSavedQueriesQuery();
  const { data: playback = null } = useGetPlaybackQualityQuery(7);
  const [runQuery] = useLazyRunSavedQueryQuery();
  const [importMusicCall, { isLoading: importing }] = useImportMusicMutation();

  const [result, setResult] = useState<QueryResult | null>(null);
  const [running, setRunning] = useState<number | null>(null);
  const [imported, setImported] = useState<MusicImportResult | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const loadError = popularError ?? engagingError ?? usersError ?? queriesError;
  const error = actionError ?? (loadError ? catalogError(loadError, 'Could not load the insights.') : null);

  const run = async (number: number) => {
    setActionError(null);
    setRunning(number);
    try {
      setResult(await runQuery(number).unwrap());
    } catch (err) {
      setActionError(catalogError(err, `Query ${number} failed.`));
    } finally {
      setRunning(null);
    }
  };

  const importMusic = async () => {
    setActionError(null);
    setImported(null);
    try {
      setImported(await importMusicCall().unwrap());
    } catch (err) {
      setActionError(catalogError(err, 'The import failed.'));
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

      <section className="stats-section">
        <h2>Song and comment models</h2>
        <MlPanels />
      </section>

      <section className="stats-section">
        <h2>Flagged comments</h2>
        <ModerationQueue />
      </section>
    </div>
  );
};

export default InsightsPage;
