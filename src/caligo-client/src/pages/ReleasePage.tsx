import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatDay, formatDuration, statusOf } from '../catalog';
import BackLink from '../components/BackLink';
import RotationButton from '../components/RotationButton';
import TrackList from '../components/TrackList';
import { useAuth } from '../contexts/AuthContext';
import { THESAURARIUM, artistPath } from '../thesaurarium';
import type { ReleaseDetail } from '../types';

const ReleasePage = ({ kind }: { kind: 'album' | 'single' }) => {
  const { id } = useParams<{ id: string }>();
  const { hasRole } = useAuth();

  const [release, setRelease] = useState<ReleaseDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [coverFailed, setCoverFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;

    setRelease(null);
    setError(null);
    setCoverFailed(false);

    api
      .get<ReleaseDetail>(`/api/${kind}s/${id}`)
      .then(response => {
        if (!cancelled) setRelease(response.data);
      })
      .catch(err => {
        if (!cancelled) {
          setError(statusOf(err) === 404 ? `This ${kind} is not in the Thesaurarium.` : catalogError(err, `Could not load the ${kind}.`));
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
        <BackLink fallback={THESAURARIUM} />
      </div>
    );
  }

  if (!release) {
    return <p className="status-line">Loading...</p>;
  }

  const minutes = release.tracks.reduce((total, track) => total + track.duration, 0);
  const releaseId = Number(id);

  return (
    <article className="post-detail">
      <BackLink fallback={THESAURARIUM} />

      <div className="portrait-header">
        <span className="release-cover">
          {release.coverUri && !coverFailed ? (
            <img src={release.coverUri} alt="" onError={() => setCoverFailed(true)} />
          ) : (
            <span className="cover-placeholder" aria-hidden="true">No Cover</span>
          )}
        </span>
        <div>
          <h1>{release.title}</h1>
          <p className="post-meta">
            {kind === 'album' ? 'Album' : 'Single'}
            {release.artists.map(artist => (
              <span key={artist.id}>
                {' · '}
                <Link to={artistPath(artist.id)}>{artist.name}</Link>
              </span>
            ))}
            {release.releaseDate ? ` · ${formatDay(release.releaseDate)}` : ''}
            {` · ${release.tracks.length} song${release.tracks.length === 1 ? '' : 's'}`}
            {minutes > 0 ? ` · ${formatDuration(minutes, 'other')}` : ''}
          </p>
        </div>
      </div>

      {release.description && <p className="content-description">{release.description}</p>}

      {hasRole('Admin') && (
        <RotationButton albumId={kind === 'album' ? releaseId : undefined} singleId={kind === 'single' ? releaseId : undefined} />
      )}

      <section className="catalog-section">
        <TrackList tracks={release.tracks} album={release.title} cover={release.coverUri} download={release.title} />
      </section>
    </article>
  );
};

export default ReleasePage;
