import { Link, useParams } from 'react-router-dom';
import { useGetActorQuery } from '../api/catalogApi';
import { catalogError, formatDay, statusOf } from '../catalog';
import ContentCard from '../components/ContentCard';
import DetailHeader from '../components/DetailHeader';
import { TITLES } from '../thesaurarium';

const ActorPage = () => {
  const { id = '' } = useParams<{ id: string }>();

  const { currentData: actor, error: loadError } = useGetActorQuery(id);
  const error = loadError
    ? statusOf(loadError) === 404 ? 'This actor is not in the Thesaurarium.' : catalogError(loadError, 'Could not load the actor.')
    : null;

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to={TITLES}>← Back to the Thesaurarium</Link>
      </div>
    );
  }

  if (!actor) {
    return <p className="status-line">Loading…</p>;
  }

  return (
    <article className="post-detail">
      <Link to={TITLES} className="back-link">← Thesaurarium</Link>

      <DetailHeader cover={actor.profilePictureUri} title={actor.name} kind="Actor">
        <p className="post-meta">
          Born {formatDay(actor.dob)} · {actor.nationality} · {actor.gender}
        </p>
      </DetailHeader>

      <p className="content-description">{actor.biography}</p>

      <section className="catalog-section">
        <h2>In the Thesaurarium</h2>
        {actor.contents.length === 0 && <p className="status-line">Nothing yet.</p>}
        {actor.contents.length > 0 && (
          <ul className="album-grid">
            {actor.contents.map(content => (
              <li key={content.contentId}>
                <ContentCard content={content} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </article>
  );
};

export default ActorPage;
