import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, yearOf } from '../catalog';
import CatalogNav from '../components/CatalogNav';
import type { SeriesSummary } from '../types';

const SeriesListPage = () => {
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

  return (
    <div>
      <CatalogNav />

      {loading && <p className="status-line">Loading series…</p>}
      {error && <p className="form-error">{error}</p>}
      {!loading && !error && series.length === 0 && <p className="status-line">No series yet.</p>}

      {series.length > 0 && (
        <div className="card-grid">
          {series.map(item => (
            <Link key={item.seriesId} to={`/series/${item.seriesId}`} className="card content-card">
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
