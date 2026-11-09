import { useGetMlMonitoringQuery } from '../api/mlApi';
import { apiError } from '../api/errors';
import DataTable from './DataTable';

const ratio = (value: number | null) => (value === null ? '—' : value.toFixed(3));

const DRIFT_KINDS: Record<string, string> = {
  prediction: 'predictions vs training',
  prediction_week: 'predictions, this week vs last'
};

const MlPanels = () => {
  const { currentData: monitoring, error } = useGetMlMonitoringQuery();

  if (error) {
    return <p className="form-error">{apiError(error, 'Could not load the model report.')}</p>;
  }

  if (!monitoring) {
    return <p className="status-line">Loading…</p>;
  }

  return (
    <div className="ml-panels">
      <h3>Models in use</h3>
      {monitoring.models.length === 0 ? (
        <p className="status-line">No model has been promoted yet.</p>
      ) : (
        <DataTable
          columns={['Task', 'Version', 'Model', 'Metric', 'Test score']}
          rows={monitoring.models.map(model => [model.task, model.version, model.candidate, model.metric, model.test])}
        />
      )}

      <h3>Drift</h3>
      {monitoring.drift.length === 0 ? (
        <p className="status-line">No drift report yet.</p>
      ) : (
        <DataTable
          columns={['Task', 'Checked', 'PSI', 'Status', 'Rows']}
          rows={monitoring.drift.map(check => [
            check.task,
            DRIFT_KINDS[check.kind] ? `${check.name} (${DRIFT_KINDS[check.kind]})` : check.name,
            check.psi,
            check.status,
            check.rows
          ])}
        />
      )}

      <h3>Last 7 days</h3>
      <DataTable
        columns={['Task', 'Source', 'Predictions']}
        rows={[
          ...monitoring.predictions.map(row => [row.task, row.source, row.count]),
          ...Object.entries(monitoring.rejected).map(([source, count]) => [source, 'rejected', count])
        ]}
      />

      <h3>Checked by admins</h3>
      <p className="post-meta">
        {`Comments: ${monitoring.moderation.labelled} decided, precision ${ratio(monitoring.moderation.precision)}, recall ${ratio(monitoring.moderation.recall)}.`}
      </p>
      <p className="post-meta">
        {`Genres: ${monitoring.genre.labelled} corrected, accuracy ${ratio(monitoring.genre.accuracy)}.`}
      </p>

      <h3>Jobs</h3>
      <DataTable
        columns={['Job', 'Status', 'Started']}
        rows={monitoring.jobs.slice(0, 8).map(run => [run.job, run.status, new Date(run.startedAt).toLocaleString()])}
      />
    </div>
  );
};

export default MlPanels;
