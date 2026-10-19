import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatCount, formatDay, formatDuration, formatRating, kindLabel, statusOf } from '../catalog';
import CommentsSection from '../components/CommentsSection';
import MediaAdmin from '../components/MediaAdmin';
import ReviewsSection from '../components/ReviewsSection';
import VideoPlayer from '../components/VideoPlayer';
import YourActivity from '../components/YourActivity';
import { useAuth } from '../contexts/AuthContext';
import type { ContentDetail, MediaInfo, SimilarContent } from '../types';

const plural = (count: number, word: string) => `${count.toLocaleString()} ${word}${count === 1 ? '' : 's'}`;

const ContentDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const { hasRole } = useAuth();
  const isAdmin = hasRole('Admin');

  const [content, setContent] = useState<ContentDetail | null>(null);
  const [similar, setSimilar] = useState<SimilarContent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [media, setMedia] = useState<MediaInfo | null>(null);

  const request = useRef(0);
  const audio = useRef<HTMLAudioElement>(null);

  const load = useCallback(async () => {
    const current = ++request.current;

    try {
      const response = await api.get<ContentDetail>(`/api/content/${id}`);
      if (current !== request.current) return;
      setContent(response.data);
      setError(null);
    } catch (err) {
      if (current !== request.current) return;
      setError(statusOf(err) === 404
        ? 'This title is not in the catalog.'
        : catalogError(err, 'Could not load the title.'));
    }
  }, [id]);

  useEffect(() => {
    let cancelled = false;

    setContent(null);
    setSimilar([]);
    setError(null);
    setMedia(null);

    load();

    api
      .get<SimilarContent[]>(`/api/content/${id}/similar`)
      .then(response => {
        if (!cancelled) setSimilar(response.data.filter(item => item.similarity > 0));
      })
      .catch(() => undefined);

    api
      .get<MediaInfo>(`/api/content/${id}/media`)
      .then(response => {
        if (!cancelled) setMedia(response.data);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [id, load]);

  const mediaChanged = useCallback((next: MediaInfo | null) => setMedia(next), []);

  if (error) {
    return (
      <div>
        <p className="form-error">{error}</p>
        <Link to="/catalog">← Back to the catalog</Link>
      </div>
    );
  }

  if (!content) {
    return <p className="status-line">Loading…</p>;
  }

  const { movie, episode, song } = content;

  return (
    <article className="post-detail">
      <Link to="/catalog" className="back-link">← Catalog</Link>

      <h1>{content.title}</h1>
      <p className="post-meta">
        {kindLabel(content.kind)} · {formatDay(content.releaseDate)} · {formatDuration(content.duration, content.kind)} · {formatRating(content.averageRating)}
      </p>
      <p className="post-meta">
        {plural(content.views, content.kind === 'song' ? 'listen' : 'view')} · {plural(content.interactions, 'reaction')} · {plural(content.comments, 'comment')}
      </p>

      {content.genres.length > 0 && (
        <p className="post-tags">
          {content.genres.map(genre => (
            <Link key={genre.id} to={`/catalog?genreId=${genre.id}`} className="tag">{genre.name}</Link>
          ))}
        </p>
      )}

      {content.description && <p className="content-description">{content.description}</p>}

      {media?.playable && (
        <section className="catalog-section">
          <VideoPlayer key={`${content.contentId}-${media.packagedAt ?? ''}`} contentId={content.contentId} />
        </section>
      )}

      {song?.audioUri && (
        <audio ref={audio} className="track-player" src={song.audioUri} controls preload="none" />
      )}

      <dl className="facts">
        {episode && (
          <>
            <dt>Series</dt>
            <dd>
              <Link to={`/series/${episode.seriesId}`}>{episode.seriesTitle}</Link>
              {' · '}season {episode.seasonNumber}{episode.seasonName ? ` (${episode.seasonName})` : ''}, episode {episode.episodeNumber}
            </dd>
          </>
        )}

        {song && song.artists.length > 0 && (
          <>
            <dt>{song.artists.length === 1 ? 'Artist' : 'Artists'}</dt>
            <dd>
              {song.artists.map((artist, index) => (
                <span key={`${artist.artistId}-${artist.role ?? ''}`}>
                  {index > 0 && ', '}
                  <Link to={`/artists/${artist.artistId}`}>{artist.name}</Link>
                  {artist.role && artist.role !== 'primary' ? ` (${artist.role})` : ''}
                </span>
              ))}
            </dd>
          </>
        )}

        {song && song.albums.length > 0 && (
          <>
            <dt>{song.albums.length === 1 ? 'Album' : 'Albums'}</dt>
            <dd>
              {song.albums.map((album, index) => (
                <span key={album.id}>
                  {index > 0 && ', '}
                  <Link to={`/albums/${album.id}`}>{album.title}</Link>
                  {album.trackNumber != null ? ` (track ${album.trackNumber})` : ''}
                </span>
              ))}
            </dd>
          </>
        )}

        {song && song.singles.length > 0 && (
          <>
            <dt>{song.singles.length === 1 ? 'Single' : 'Singles'}</dt>
            <dd>
              {song.singles.map((single, index) => (
                <span key={single.id}>
                  {index > 0 && ', '}
                  <Link to={`/singles/${single.id}`}>{single.title}</Link>
                </span>
              ))}
            </dd>
          </>
        )}

        {content.actors.length > 0 && (
          <>
            <dt>Cast</dt>
            <dd>
              {content.actors.map((actor, index) => (
                <span key={actor.actorId}>
                  {index > 0 && ', '}
                  <Link to={`/actors/${actor.actorId}`}>{actor.name}</Link>
                </span>
              ))}
            </dd>
          </>
        )}

        <dt>{content.kind === 'song' ? 'Label' : 'Production'}</dt>
        <dd>{content.productionCompany.name}</dd>

        {content.distributingCompanies.length > 0 && (
          <>
            <dt>Distribution</dt>
            <dd>{content.distributingCompanies.map(company => company.name).join(', ')}</dd>
          </>
        )}

        {content.languages.length > 0 && (
          <>
            <dt>{content.languages.length === 1 ? 'Language' : 'Languages'}</dt>
            <dd>{content.languages.map(language => language.name).join(', ')}</dd>
          </>
        )}

        {content.subtitles.length > 0 && (
          <>
            <dt>Subtitles</dt>
            <dd>{content.subtitles.map(subtitle => subtitle.name).join(', ')}</dd>
          </>
        )}

        {movie?.budget != null && (
          <>
            <dt>Budget</dt>
            <dd>{formatCount(movie.budget)}</dd>
          </>
        )}

        {movie?.boxOffice != null && (
          <>
            <dt>Box office</dt>
            <dd>{formatCount(movie.boxOffice)}</dd>
          </>
        )}
      </dl>

      {song?.lyrics && (
        <section className="catalog-section">
          <h2>Lyrics</h2>
          <p className="lyrics">{song.lyrics}</p>
        </section>
      )}

      <section className="catalog-section">
        <h2>Yours</h2>
        <YourActivity content={content} onChanged={load} position={() => audio.current?.currentTime ?? null} />
      </section>

      {similar.length > 0 && (
        <section className="catalog-section">
          <h2>Similar titles</h2>
          <ol className="trending-list">
            {similar.map(item => (
              <li key={item.contentId}>
                <Link to={`/catalog/${item.contentId}`}>{item.title}</Link>
                <span className="trending-score" title="Share of genres in common">
                  {Math.round(item.similarity * 100)}%
                </span>
              </li>
            ))}
          </ol>
        </section>
      )}

      {isAdmin && content.kind !== 'song' && <MediaAdmin contentId={content.contentId} onChanged={mediaChanged} />}

      <ReviewsSection contentId={content.contentId} />
      <CommentsSection contentId={content.contentId} onPosted={load} />
    </article>
  );
};

export default ContentDetailPage;
