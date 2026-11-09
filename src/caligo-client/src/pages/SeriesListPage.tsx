import { useSearchParams } from 'react-router-dom';
import { useGetSeriesListQuery } from '../api/catalogApi';
import { catalogError, yearOf } from '../catalog';
import CoverCard from '../components/CoverCard';
import ThesaurariumNav from '../components/ThesaurariumNav';
import { seriesPath } from '../thesaurarium';

const SeriesListPage = () => {
  const [params] = useSearchParams();
  const query = (params.get('query') ?? '').toLowerCase();

  const { data: series = [], error: loadError, isLoading: loading } = useGetSeriesListQuery();
  const error = loadError ? catalogError(loadError, 'Could not load the series.') : null;

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
        <ul className="album-grid">
          {shown.map(item => (
            <li key={item.seriesId}>
              <CoverCard
                to={seriesPath(item.seriesId)}
                cover={null}
                kind="Series"
                title={item.title}
                sub={['Series', yearOf(item.releaseDate)].filter(part => part.length > 0).join(' · ')}
                meta={`${item.seasons} season${item.seasons === 1 ? '' : 's'}`}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};

export default SeriesListPage;
