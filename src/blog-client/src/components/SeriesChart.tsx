import { parseServerDate } from '../dates';
import type { SeriesBucket, SeriesPoint } from '../types';
import BarLineChart from './BarLineChart';

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

  const totalViews = buckets.reduce((sum, b) => sum + b.views, 0);
  const totalClaps = buckets.reduce((sum, b) => sum + b.claps, 0);

  return (
    <BarLineChart
      slots={buckets.map(b => ({
        key: String(b.start.getTime()),
        label: axisLabel(b.start, bucket),
        title: `${fullLabel(b.start, bucket)}: ${b.views} view${b.views === 1 ? '' : 's'}, `
          + `${b.visitors} visitor${b.visitors === 1 ? '' : 's'}, ${b.claps} clap${b.claps === 1 ? '' : 's'}`,
        bar: b.views,
        line: b.claps
      }))}
      barLegend="views (left)"
      lineLegend="claps (right)"
      ariaLabel={`${totalViews} views and ${totalClaps} claps over ${buckets.length} ${bucket}s`}
    />
  );
};

export default SeriesChart;
