import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import api from '../api/axios';
import { apiError } from '../api/errors';
import BarLineChart from '../components/BarLineChart';
import DataTable from '../components/DataTable';
import { formatDateTime, parseServerDate } from '../dates';
import type { LabContestant, LabRunDetail, LabRunsResponse } from '../types';

const milliseconds = (value: number | null) => (value === null ? null : Math.round(value));

const megabytes = (value: number | null) => (value === null ? null : Math.round(value / 1048576));

const percent = (value: number | null) => (value === null ? null : Math.round(value * 10) / 10);

const describe = (row: LabContestant) =>
  `${row.contestant} (${row.protocol} on ${row.server}): latency ${milliseconds(row.latencyP50Ms) ?? '—'} ms, `
  + `start-up ${milliseconds(row.startupP50Ms) ?? '—'} ms, ${row.connected}/${row.listeners} connected`;

const LabRunPage = () => {
  const { runId } = useParams<{ runId: string }>();
  const navigate = useNavigate();
  const [runs, setRuns] = useState<LabRunsResponse | null>(null);
  const [detail, setDetail] = useState<LabRunDetail | null>(null);
  const [focus, setFocus] = useState<string>('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    api.get<LabRunsResponse>('/api/lab/runs')
      .then(response => {
        if (cancelled) return;

        setRuns(response.data);

        if (!runId && response.data.runs.length > 0) {
          navigate(`/lab/${response.data.runs[0].runId}`, { replace: true });
        }
      })
      .catch(err => {
        if (!cancelled) setError(apiError(err, 'Could not load the lab runs.'));
      });

    return () => {
      cancelled = true;
    };
  }, [runId, navigate]);

  useEffect(() => {
    if (!runId) {
      setDetail(null);

      return;
    }

    let cancelled = false;

    api.get<LabRunDetail>(`/api/lab/runs/${runId}`)
      .then(response => {
        if (cancelled) return;

        setDetail(response.data);
        setFocus(current => current || response.data.contestants.find(row => row.latencyP50Ms !== null)?.contestant || '');
      })
      .catch(err => {
        if (!cancelled) setError(apiError(err, 'Could not load this run.'));
      });

    return () => {
      cancelled = true;
    };
  }, [runId]);

  const ranked = useMemo(
    () => [...(detail?.contestants ?? [])].sort((left, right) =>
      (left.latencyP50Ms ?? Number.MAX_VALUE) - (right.latencyP50Ms ?? Number.MAX_VALUE)
      || left.contestant.localeCompare(right.contestant)),
    [detail]);

  const timeline = useMemo(() => {
    const points = (detail?.latency ?? []).filter(point => point.contestant === focus);
    const first = points.length > 0 ? parseServerDate(points[0].measuredAt).getTime() : 0;

    return points.map(point => {
      const offset = Math.round((parseServerDate(point.measuredAt).getTime() - first) / 1000);
      const label = `${Math.floor(offset / 60)}:${String(offset % 60).padStart(2, '0')}`;

      return {
        key: `${point.repetition}-${point.measuredAt}`,
        label,
        title: `repetition ${point.repetition}, ${label}: ${Math.round(point.latencyMs)} ms`,
        bar: point.latencyMs,
        line: null
      };
    });
  }, [detail, focus]);

  const run = detail?.run;

  return (
    <div className="stats-page">
      <h1>Streaming lab</h1>
      <p className="lede">
        Every way of streaming the radio, fed by one source and measured the same way: listeners per contestant, a shared
        playback model for start-up and stalls, coded beeps for latency, and the server's CPU and memory while it is under
        load. Start a run with <code>deploy/lab/run-lab.sh &lt;scenario&gt;</code>.
      </p>

      {error && <p className="form-error">{error}</p>}
      {runs && !runs.enabled && <p className="status-line">Lab results are switched off on this server.</p>}
      {runs?.enabled && runs.runs.length === 0 && <p className="status-line">No run has been recorded yet.</p>}

      {runs && runs.runs.length > 0 && (
        <label className="lab-picker">
          <span>Run</span>
          <select value={runId ?? ''} onChange={event => navigate(`/lab/${event.target.value}`)}>
            {runs.runs.map(item => (
              <option key={item.runId} value={item.runId}>
                {formatDateTime(item.startedAt)} · {item.scenario} · {item.contestants} contestants
              </option>
            ))}
          </select>
        </label>
      )}

      {run && detail && (
        <>
          <section className="stats-section">
            <h2>{run.scenario}</h2>
            <p className="hint">
              {run.listeners} listeners for {run.seconds} s, {run.repetitions} repetition{run.repetitions === 1 ? '' : 's'},
              impairment <code>{run.profile}</code>
              {run.sourceBiasMs !== null && <> · the source's beeps left {Math.round(run.sourceBiasMs)} ms after each second</>}
              {' '}· {formatDateTime(run.startedAt)}
            </p>
            {ranked.length > 0 && (
              <BarLineChart
                slots={ranked.map(row => ({
                  key: row.contestant,
                  label: row.contestant,
                  title: describe(row),
                  bar: row.latencyP50Ms,
                  line: row.startupP50Ms
                }))}
                barLegend="latency p50 (left)"
                lineLegend="start-up p50 (right)"
                ariaLabel={`Latency and start-up of ${ranked.length} contestants`}
                unit=" ms"
                everyLabel
              />
            )}
          </section>

          <section className="stats-section">
            <h2>Contestants</h2>
            <p className="hint">
              Medians over every listener; spread is the gap between the best and worst repetition. Latency is measured from
              the source's clock to the moment the audio would play, on one probe listener per contestant (every browser
              listener for WebRTC); it is empty where the stream does not carry the lab's signal.
            </p>
            <DataTable
              columns={[
                'contestant', 'protocol', 'server', 'connected', 'first byte ms', 'start-up p50 ms', 'start-up p95 ms',
                'start-up spread ms', 'stalls / listener-min', 'latency p50 ms', 'latency spread ms', 'data latency ms',
                'CPU mean %', 'CPU peak %', 'memory MB', 'impaired packets / lost / overflowed', 'errors'
              ]}
              rows={ranked.map(row => [
                row.contestant,
                row.protocol,
                row.server,
                `${row.connected}/${row.listeners}`,
                milliseconds(row.ttfbP50Ms),
                milliseconds(row.startupP50Ms),
                milliseconds(row.startupP95Ms),
                milliseconds(row.startupSpreadMs),
                row.stallsPerListenerMinute,
                milliseconds(row.latencyP50Ms),
                milliseconds(row.latencySpreadMs),
                milliseconds(row.dataLatencyP50Ms),
                percent(row.cpuMeanPercent),
                percent(row.cpuPeakPercent),
                megabytes(row.memoryPeakBytes),
                `${row.impairedPackets} / ${row.impairedLost} / ${row.impairedOverflowed}`,
                row.errors || null
              ])}
            />
          </section>

          <section className="stats-section">
            <h2>Latency over the run</h2>
            <label className="lab-picker">
              <span>Contestant</span>
              <select value={focus} onChange={event => setFocus(event.target.value)}>
                {ranked.filter(row => row.latencyP50Ms !== null).map(row => (
                  <option key={row.contestant} value={row.contestant}>{row.contestant}</option>
                ))}
              </select>
            </label>
            {timeline.length === 0
              ? <p className="status-line">No latency samples for this contestant.</p>
              : (
                <BarLineChart
                  slots={timeline}
                  barLegend="latency per second"
                  ariaLabel={`Latency of ${focus} over ${timeline.length} seconds`}
                  unit=" ms"
                />
              )}
          </section>
        </>
      )}
    </div>
  );
};

export default LabRunPage;
