import { parseServerDate } from '../dates';
import type { SeriesBucket, SeriesPoint } from '../types';

const WIDTH = 640;
const HEIGHT = 220;
const PAD = { top: 14, right: 42, bottom: 26, left: 42 };

const STEP_MS: Record<SeriesBucket, number> = {
  minute: 60 * 1000,
  hour: 60 * 60 * 1000,
  day: 24 * 60 * 60 * 1000
};

const MAX_BUCKETS = 400;

interface Bucket {
  start: Date;
  views: number;
  visitors: number;
  claps: number;
}

const fillBuckets = (points: SeriesPoint[], bucket: SeriesBucket, from: string, to: string): Bucket[] => {
  const step = STEP_MS[bucket];
  const byStart = new Map(points.map(point => [parseServerDate(point.bucketStart).getTime(), point]));
  const end = parseServerDate(to).getTime();
  const buckets: Bucket[] = [];

  for (let start = Math.floor(parseServerDate(from).getTime() / step) * step;
       start < end && buckets.length < MAX_BUCKETS;
       start += step) {
    const point = byStart.get(start);

    buckets.push({
      start: new Date(start),
      views: point?.views ?? 0,
      visitors: point?.uniqueVisitors ?? 0,
      claps: point?.claps ?? 0
    });
  }

  return buckets;
};

const axisLabel = (date: Date, bucket: SeriesBucket) =>
  bucket === 'day'
    ? date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
    : date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });

const fullLabel = (date: Date, bucket: SeriesBucket) =>
  bucket === 'day'
    ? date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
    : date.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });

interface SeriesChartProps {
  points: SeriesPoint[];
  bucket: SeriesBucket;
  from: string;
  to: string;
}

const SeriesChart = ({ points, bucket, from, to }: SeriesChartProps) => {
  const buckets = fillBuckets(points, bucket, from, to);

  if (buckets.length === 0 || buckets.every(b => b.views === 0 && b.claps === 0)) {
    return <p className="status-line">No views or claps in this period yet.</p>;
  }

  const plotWidth = WIDTH - PAD.left - PAD.right;
  const plotHeight = HEIGHT - PAD.top - PAD.bottom;
  const slot = plotWidth / buckets.length;
  const barWidth = Math.max(1, slot * 0.7);

  const maxViews = Math.max(1, ...buckets.map(b => b.views));
  const maxClaps = Math.max(1, ...buckets.map(b => b.claps));

  const x = (i: number) => PAD.left + i * slot;
  const y = (value: number, max: number) => PAD.top + plotHeight - (value / max) * plotHeight;

  const clapLine = buckets
    .map((b, i) => `${i === 0 ? 'M' : 'L'}${(x(i) + slot / 2).toFixed(1)},${y(b.claps, maxClaps).toFixed(1)}`)
    .join(' ');

  const labelled = [...new Set([0, Math.floor((buckets.length - 1) / 2), buckets.length - 1])];
  const totalViews = buckets.reduce((sum, b) => sum + b.views, 0);
  const totalClaps = buckets.reduce((sum, b) => sum + b.claps, 0);

  return (
    <figure className="series">
      <svg
        className="series-chart"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={`${totalViews} views and ${totalClaps} claps over ${buckets.length} ${bucket}s`}
      >
        <line className="series-gridline" x1={PAD.left} x2={WIDTH - PAD.right} y1={PAD.top} y2={PAD.top} />
        <line
          className="series-gridline"
          x1={PAD.left}
          x2={WIDTH - PAD.right}
          y1={PAD.top + plotHeight}
          y2={PAD.top + plotHeight}
        />

        <text className="series-axis" x={PAD.left - 6} y={PAD.top + 4} textAnchor="end">{maxViews}</text>
        <text className="series-axis" x={PAD.left - 6} y={PAD.top + plotHeight} textAnchor="end">0</text>
        <text className="series-axis" x={WIDTH - PAD.right + 6} y={PAD.top + 4}>{maxClaps}</text>
        <text className="series-axis" x={WIDTH - PAD.right + 6} y={PAD.top + plotHeight}>0</text>

        {buckets.map((b, i) => (
          <g key={b.start.getTime()} className="series-slot">
            <title>
              {`${fullLabel(b.start, bucket)}: ${b.views} view${b.views === 1 ? '' : 's'}, `
                + `${b.visitors} visitor${b.visitors === 1 ? '' : 's'}, ${b.claps} clap${b.claps === 1 ? '' : 's'}`}
            </title>
            <rect className="series-hit" x={x(i)} y={PAD.top} width={slot} height={plotHeight} />
            {b.views > 0 && (
              <rect
                className="series-bar"
                x={x(i) + (slot - barWidth) / 2}
                y={y(b.views, maxViews)}
                width={barWidth}
                height={PAD.top + plotHeight - y(b.views, maxViews)}
              />
            )}
          </g>
        ))}

        <path className="series-line" d={clapLine} />

        {labelled.map(i => (
          <text
            key={i}
            className="series-axis"
            x={x(i) + slot / 2}
            y={HEIGHT - 8}
            textAnchor={i === 0 ? 'start' : i === buckets.length - 1 ? 'end' : 'middle'}
          >
            {axisLabel(buckets[i].start, bucket)}
          </text>
        ))}
      </svg>

      <figcaption className="series-legend">
        <span><span className="legend-swatch legend-views" />views (left)</span>
        <span><span className="legend-swatch legend-claps" />claps (right)</span>
      </figcaption>
    </figure>
  );
};

export default SeriesChart;
