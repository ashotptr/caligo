import type { CSSProperties } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useGetReleaseQuery } from '../api/catalogApi';
import { useGetAlbumAnalysisQuery } from '../api/mlApi';
import { catalogError, formatDay, formatDuration, statusOf } from '../catalog';
import BackLink from '../components/BackLink';
import DetailHeader from '../components/DetailHeader';
import RotationButton from '../components/RotationButton';
import TrackList from '../components/TrackList';
import { mixtapeFolder } from '../ml';
import { useAuth } from '../store/hooks';
import { THESAURARIUM, artistPath } from '../thesaurarium';

const ReleasePage = ({ kind }: { kind: 'album' | 'single' }) => {
  const { id = '' } = useParams<{ id: string }>();
  const { hasRole } = useAuth();

  const { currentData: release, error: loadError } = useGetReleaseQuery({ kind, id });
  const folder = mixtapeFolder(release?.coverUri) ?? mixtapeFolder(release?.tracks.find(track => track.audioUri)?.audioUri);
  const { currentData: sound } = useGetAlbumAnalysisQuery(folder ?? '', { skip: !folder });
  const error = loadError
    ? statusOf(loadError) === 404 ? `This ${kind} is not in the Thesaurarium.` : catalogError(loadError, `Could not load the ${kind}.`)
    : null;

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
  const sounds = Object.fromEntries((sound?.songs ?? []).map(song => [song.uri, song]));
  const accent = sound?.accent ? ({ '--album-accent': sound.accent } as CSSProperties) : undefined;

  return (
    <article className={accent ? 'post-detail album-accented' : 'post-detail'} style={accent}>
      <BackLink fallback={THESAURARIUM} />

      <DetailHeader cover={release.coverUri} title={release.title} kind={kind === 'album' ? 'Album' : 'Single'}>
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
      </DetailHeader>

      {release.description && <p className="content-description">{release.description}</p>}

      {hasRole('Admin') && (
        <RotationButton albumId={kind === 'album' ? releaseId : undefined} singleId={kind === 'single' ? releaseId : undefined} />
      )}

      <section className="catalog-section">
        <TrackList
          tracks={release.tracks}
          album={release.title}
          cover={release.coverUri}
          download={release.title}
          sounds={sounds}
        />
      </section>
    </article>
  );
};

export default ReleasePage;
