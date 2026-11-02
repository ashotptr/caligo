import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, statusOf } from '../catalog';
import ContentCard from '../components/ContentCard';
import DetailHeader from '../components/DetailHeader';
import { TITLES } from '../thesaurarium';
import type { ActorDetail } from '../types';

const ActorPage = () => {
  const { id } = useParams<{ id: string }>();

  const [actor, setActor] = useState<ActorDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setActor(null);
    setError(null);

    api
      .get<ActorDetail>(`/api/actors/${id}`)
      .then(response => {
        if (!cancelled) setActor(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? 'This actor is not in the Thesaurarium.' : catalogError(err, 'Could not load the actor.'));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id]);

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
