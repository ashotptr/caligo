import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, statusOf } from '../catalog';
import Artwork from '../components/Artwork';
import ContentCard from '../components/ContentCard';
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
          setError(statusOf(err) === 404 ? 'This actor is not in the catalog.' : catalogError(err, 'Could not load the actor.'));
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
        <Link to="/catalog">← Back to the catalog</Link>
      </div>
    );
  }

  if (!actor) {
    return <p className="status-line">Loading…</p>;
  }

  return (
    <article className="post-detail">
      <Link to="/catalog" className="back-link">← Catalog</Link>

      <div className="portrait-header">
        <Artwork src={actor.profilePictureUri} className="portrait" />
        <div>
          <h1>{actor.name}</h1>
          <p className="post-meta">
            Born {formatDay(actor.dob)} · {actor.nationality} · {actor.gender}
          </p>
        </div>
      </div>

      <p className="content-description">{actor.biography}</p>

      <section className="catalog-section">
        <h2>In the catalog</h2>
        {actor.contents.length === 0 && <p className="status-line">Nothing yet.</p>}
        {actor.contents.length > 0 && (
          <div className="card-grid">
            {actor.contents.map(content => (
              <ContentCard key={content.contentId} content={content} />
            ))}
          </div>
        )}
      </section>
    </article>
  );
};

export default ActorPage;
