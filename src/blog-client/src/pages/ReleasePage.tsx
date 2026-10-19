import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, formatDuration, statusOf } from '../catalog';
import Artwork from '../components/Artwork';
import TrackList from '../components/TrackList';
import type { ReleaseDetail } from '../types';

const ReleasePage = ({ kind }: { kind: 'album' | 'single' }) => {
  const { id } = useParams<{ id: string }>();

  const [release, setRelease] = useState<ReleaseDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setRelease(null);
    setError(null);

    api
      .get<ReleaseDetail>(`/api/${kind}s/${id}`)
      .then(response => {
        if (!cancelled) setRelease(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? `This ${kind} is not in the catalog.` : catalogError(err, `Could not load the ${kind}.`));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id, kind]);

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to="/albums">← All albums</Link>
      </div>
    );
  }

  if (!release) {
    return <p className="status-line">Loading…</p>;
  }

  const minutes = release.tracks.reduce((total, track) => total + track.duration, 0);

  return (
    <article className="post-detail">
      <Link to="/albums" className="back-link">← All albums</Link>

      <div className="portrait-header">
        <Artwork src={release.coverUri} className="portrait cover" placeholder="♪" />
        <div>
          <h1>{release.title}</h1>
          <p className="post-meta">
            {kind === 'album' ? 'Album' : 'Single'}
            {release.artists.map(artist => (
              <span key={artist.id}>
                {' · '}
                <Link to={`/artists/${artist.id}`}>{artist.name}</Link>
              </span>
            ))}
            {release.releaseDate ? ` · ${formatDay(release.releaseDate)}` : ''}
            {` · ${release.tracks.length} song${release.tracks.length === 1 ? '' : 's'}`}
            {minutes > 0 ? ` · ${formatDuration(minutes, 'other')}` : ''}
          </p>
        </div>
      </div>

      {release.description && <p className="content-description">{release.description}</p>}

      <section className="catalog-section">
        <TrackList tracks={release.tracks} />
      </section>
    </article>
  );
};

export default ReleasePage;
