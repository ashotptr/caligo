import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, yearOf } from '../catalog';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { seriesPath } from '../thesaurarium';
import type { SeriesSummary } from '../types';

const SeriesListPage = () => {
  const [params] = useSearchParams();
  const query = (params.get('query') ?? '').toLowerCase();

  const [series, setSeries] = useState<SeriesSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .get<SeriesSummary[]>('/api/series')
      .then(response => setSeries(response.data))
      .catch(err => setError(catalogError(err, 'Could not load the series.')))
      .finally(() => setLoading(false));
  }, []);

  const shown = series.filter(item => item.title.toLowerCase().includes(query));

  return (
    <div>
      <ThesaurariumNav />

      {loading && <p className="status-line">Loading series…</p>}
      {error && <p className="form-error">{error}</p>}
      {!loading && !error && shown.length === 0 && (
        <p className="status-line">{query ? 'Nothing found.' : 'No series yet.'}</p>
      )}

      {shown.length > 0 && (
        <div className="card-grid">
          {shown.map(item => (
            <Link key={item.seriesId} to={seriesPath(item.seriesId)} className="card content-card">
              <span className="card-kind">Series · {yearOf(item.releaseDate)}</span>
              <span className="card-title">{item.title}</span>
              <span className="card-sub">{item.seasons} season{item.seasons === 1 ? '' : 's'}</span>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
};

export default SeriesListPage;
