import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, formatDuration, statusOf } from '../catalog';
import { SERIES, titlePath } from '../thesaurarium';
import type { SeriesDetail } from '../types';

const SeriesDetailPage = () => {
  const { id } = useParams<{ id: string }>();

  const [series, setSeries] = useState<SeriesDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setSeries(null);
    setError(null);

    api
      .get<SeriesDetail>(`/api/series/${id}`)
      .then(response => {
        if (!cancelled) setSeries(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? 'This series is not in the Thesaurarium.' : catalogError(err, 'Could not load the series.'));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id]);

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to={SERIES}>← All series</Link>
      </div>
    );
  }

  if (!series) {
    return <p className="status-line">Loading…</p>;
  }

  return (
    <article className="post-detail">
      <Link to={SERIES} className="back-link">← All series</Link>

      <h1>{series.title}</h1>
      <p className="post-meta">
        Series · {formatDay(series.releaseDate)} · {series.seasons.length} season{series.seasons.length === 1 ? '' : 's'}
      </p>

      <p className="content-description">{series.description}</p>

      {series.seasons.map(season => (
        <section key={season.seasonNumber} className="catalog-section">
          <h2>
            Season {season.seasonNumber}
            {season.name ? <span className="hint"> · {season.name}</span> : null}
          </h2>

          {season.episodes.length === 0 ? (
            <p className="status-line">No episodes in the Thesaurarium yet.</p>
          ) : (
            <ol className="track-list">
              {season.episodes.map(episode => (
                <li key={episode.contentId}>
                  <span className="track-number">{episode.episodeNumber}</span>
                  <span className="track-title">
                    <Link to={titlePath(episode.contentId)}>{episode.title}</Link>
                  </span>
                  <span className="track-duration">{formatDuration(episode.duration, 'episode')}</span>
                </li>
              ))}
            </ol>
          )}
        </section>
      ))}
    </article>
  );
};

export default SeriesDetailPage;
