const WIDTH = 640;
const HEIGHT = 220;
const LABEL_SPACE = 96;
const PAD = { top: 14, bottom: 26 };
const SIDE = 42;
const WIDE_SIDE = 58;

export interface BarLineSlot {
  key: string;
  label: string;
  title: string;
  bar: number | null;
  line: number | null;
}

interface BarLineChartProps {
  slots: BarLineSlot[];
  barLegend: string;
  lineLegend?: string;
  ariaLabel: string;
  unit?: string;
  everyLabel?: boolean;
}

const format = (value: number, unit: string) => unit ? `${Math.round(value).toLocaleString()}${unit}` : String(Math.round(value));

const BarLineChart = ({ slots, barLegend, lineLegend, ariaLabel, unit = '', everyLabel = false }: BarLineChartProps) => {
  const side = unit ? WIDE_SIDE : SIDE;
  const left = side;
  const right = side;
  const height = HEIGHT + (everyLabel ? LABEL_SPACE : 0);
  const bottom = PAD.bottom + (everyLabel ? LABEL_SPACE : 0);
  const plotWidth = WIDTH - left - right;
  const plotHeight = height - PAD.top - bottom;
  const slot = plotWidth / Math.max(1, slots.length);
  const barWidth = Math.max(1, slot * 0.7);
  const bars = slots.map(item => item.bar).filter((value): value is number => value !== null);
  const lines = slots.map(item => item.line).filter((value): value is number => value !== null);
  const maxBar = Math.max(1, ...bars);
  const maxLine = Math.max(1, ...lines);

  const x = (i: number) => left + i * slot;
  const y = (value: number, max: number) => PAD.top + plotHeight - (value / max) * plotHeight;

  let pen = 'M';
  const path = slots
    .map((item, i) => {
      if (item.line === null) {
        pen = 'M';

        return '';
      }

      const step = `${pen}${(x(i) + slot / 2).toFixed(1)},${y(item.line, maxLine).toFixed(1)}`;

      pen = 'L';

      return step;
    })
    .filter(step => step.length > 0)
    .join(' ');

  const labelled = everyLabel
    ? slots.map((_, i) => i)
    : [...new Set([0, Math.floor((slots.length - 1) / 2), slots.length - 1])].filter(i => i >= 0);

  return (
    <figure className="series">
      <svg className="series-chart" viewBox={`0 0 ${WIDTH} ${height}`} role="img" aria-label={ariaLabel}>
        <line className="series-gridline" x1={left} x2={WIDTH - right} y1={PAD.top} y2={PAD.top} />
        <line
          className="series-gridline"
          x1={left}
          x2={WIDTH - right}
          y1={PAD.top + plotHeight}
          y2={PAD.top + plotHeight}
        />

        <text className="series-axis" x={left - 6} y={PAD.top + 4} textAnchor="end">{format(maxBar, unit)}</text>
        <text className="series-axis" x={left - 6} y={PAD.top + plotHeight} textAnchor="end">0</text>
        {lineLegend && (
          <>
            <text className="series-axis" x={WIDTH - right + 6} y={PAD.top + 4}>{format(maxLine, unit)}</text>
            <text className="series-axis" x={WIDTH - right + 6} y={PAD.top + plotHeight}>0</text>
          </>
        )}

        {slots.map((item, i) => (
          <g key={item.key} className="series-slot">
            <title>{item.title}</title>
            <rect className="series-hit" x={x(i)} y={PAD.top} width={slot} height={plotHeight} />
            {item.bar !== null && item.bar > 0 && (
              <rect
                className="series-bar"
                x={x(i) + (slot - barWidth) / 2}
                y={y(item.bar, maxBar)}
                width={barWidth}
                height={PAD.top + plotHeight - y(item.bar, maxBar)}
              />
            )}
          </g>
        ))}

        {lineLegend && path.length > 0 && <path className="series-line" d={path} />}

        {labelled.map(i => everyLabel
          ? (
            <text
              key={i}
              className="series-axis"
              transform={`translate(${(x(i) + slot / 2).toFixed(1)},${PAD.top + plotHeight + 10}) rotate(-40)`}
              textAnchor="end"
            >
              {slots[i].label}
            </text>
          )
          : (
            <text
              key={i}
              className="series-axis"
              x={x(i) + slot / 2}
              y={height - 8}
              textAnchor={i === 0 ? 'start' : i === slots.length - 1 ? 'end' : 'middle'}
            >
              {slots[i].label}
            </text>
          ))}
      </svg>

      <figcaption className="series-legend">
        <span><span className="legend-swatch legend-views" />{barLegend}</span>
        {lineLegend && <span><span className="legend-swatch legend-claps" />{lineLegend}</span>}
      </figcaption>
    </figure>
  );
};

export default BarLineChart;
