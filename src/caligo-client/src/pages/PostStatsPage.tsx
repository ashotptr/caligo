import { useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { useGetPostFunnelQuery, useGetPostSeriesQuery } from '../api/analyticsApi';
import { apiError } from '../api/errors';
import SeriesChart from '../components/SeriesChart';
import type { SeriesBucket } from '../types';

const RANGES: { bucket: SeriesBucket; label: string }[] = [
  { bucket: 'minute', label: 'Last hour' },
  { bucket: 'hour', label: 'Last day' },
  { bucket: 'day', label: 'Last 30 days' }
];

const FUNNEL_DAYS = [7, 30];

const PostStatsPage = () => {
  const { id = '' } = useParams<{ id: string }>();
  const location = useLocation();
  const title = (location.state as { title?: string } | null)?.title ?? `Post #${id}`;

  const [bucket, setBucket] = useState<SeriesBucket>('hour');
  const [days, setDays] = useState(FUNNEL_DAYS[0]);

  const { currentData: series, error: seriesError } = useGetPostSeriesQuery({ id, bucket });
  const { data: funnel, error: funnelError } = useGetPostFunnelQuery({ id, days });

  const error = seriesError
    ? apiError(seriesError, 'Could not load the chart.')
    : funnelError
      ? apiError(funnelError, 'Could not load the engagement numbers.')
      : null;

  const disabled = series?.enabled === false || funnel?.enabled === false;

  const steps = funnel
    ? [
        { label: 'views', value: funnel.views },
        { label: 'unique visitors', value: funnel.uniqueVisitors },
        { label: 'clappers', value: funnel.clappers }
      ]
    : [];

  const widest = Math.max(1, funnel?.views ?? 0);

  return (
    <div className="stats-page">
      <Link to="/dashboard" className="back-link">← My Posts</Link>
      <h1><Link to={`/posts/${id}`}>{title}</Link></h1>
      <p className="post-meta">Statistics from the analytics store, updated within seconds</p>

      {error && <p className="form-error">{error}</p>}

      {disabled ? (
        <p className="notice">Statistics aren't available right now.</p>
      ) : (
        <>
          <section className="stats-section">
            <div className="stats-section-header">
              <h2>Views and claps</h2>
              <div className="tabs" role="group" aria-label="Time range">
                {RANGES.map(range => (
                  <button
                    key={range.bucket}
                    type="button"
                    aria-pressed={bucket === range.bucket}
                    onClick={() => setBucket(range.bucket)}
                  >
                    {range.label}
                  </button>
                ))}
              </div>
            </div>

            {series
              ? <SeriesChart points={series.points} bucket={series.bucket} from={series.from} to={series.to} />
              : <p className="status-line">Loading…</p>}
          </section>

          <section className="stats-section">
            <div className="stats-section-header">
              <h2>Engagement</h2>
              <div className="tabs" role="group" aria-label="Funnel range">
                {FUNNEL_DAYS.map(option => (
                  <button key={option} type="button" aria-pressed={days === option} onClick={() => setDays(option)}>
                    Last {option} days
                  </button>
                ))}
              </div>
            </div>

            {funnel ? (
              <>
                <ol className="funnel-steps">
                  {steps.map(step => (
                    <li key={step.label}>
                      <span className="funnel-label">{step.label}</span>
                      <span className="funnel-track">
                        <span
                          className="funnel-bar"
                          style={{ width: `${Math.min(100, Math.max(0.5, (step.value / widest) * 100))}%` }}
                        />
                      </span>
                      <span className="funnel-value">{step.value.toLocaleString()}</span>
                    </li>
                  ))}
                </ol>
                <p className="hint">
                  {funnel.claps.toLocaleString()} clap{funnel.claps === 1 ? '' : 's'} in total
                  {' · '}{(funnel.clapperRate * 100).toFixed(1)}% of visitors clapped
                  {' · '}unique counts are approximate
                </p>
              </>
            ) : (
              <p className="status-line">Loading…</p>
            )}
          </section>
        </>
      )}
    </div>
  );
};

export default PostStatsPage;
