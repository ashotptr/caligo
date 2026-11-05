import { plainText } from './text.js';

export type Fetch = (path: string) => Promise<unknown>;

type DetailType = 'post' | 'title' | 'series' | 'artist' | 'album' | 'single' | 'actor';

type SectionType = 'home' | 'radio' | 'thesaurarium' | 'chat-rooms' | 'omniscient';

export interface Route {
  type: DetailType | SectionType;
  id: number | null;
  path: string;
  key: string;
}

export interface Page {
  path: string;
  key: string;
  kind: string;
  label: string;
  title: string;
  description: string;
  imagePath: string | null;
}

type Details = Omit<Page, 'path' | 'key'>;

const DETAILS: { pattern: RegExp; type: DetailType; base: string }[] = [
  { pattern: /^\/posts\/(\d{1,9})$/, type: 'post', base: '/posts' },
  { pattern: /^\/thesaurarium\/titles\/(\d{1,9})$/, type: 'title', base: '/thesaurarium/titles' },
  { pattern: /^\/thesaurarium\/series\/(\d{1,9})$/, type: 'series', base: '/thesaurarium/series' },
  { pattern: /^\/thesaurarium\/artists\/(\d{1,9})$/, type: 'artist', base: '/thesaurarium/artists' },
  { pattern: /^\/thesaurarium\/albums\/(\d{1,9})$/, type: 'album', base: '/thesaurarium/albums' },
  { pattern: /^\/thesaurarium\/singles\/(\d{1,9})$/, type: 'single', base: '/thesaurarium/singles' },
  { pattern: /^\/thesaurarium\/actors\/(\d{1,9})$/, type: 'actor', base: '/thesaurarium/actors' }
];

const SECTIONS: { pattern: RegExp; type: SectionType; path: string }[] = [
  { pattern: /^\/listen$/, type: 'radio', path: '/listen' },
  { pattern: /^\/thesaurarium(\/(titles|series|artists))?$/, type: 'thesaurarium', path: '/thesaurarium' },
  { pattern: /^\/chat-rooms$/, type: 'chat-rooms', path: '/chat-rooms' },
  { pattern: /^\/omniscient$/, type: 'omniscient', path: '/omniscient' }
];

const SECTION_PAGES: Record<SectionType, Details> = {
  home: {
    kind: 'website',
    label: 'Caligo',
    title: 'Caligo',
    description: 'Radio, music, films and series, and posts.',
    imagePath: null
  },
  radio: {
    kind: 'website',
    label: 'Radio',
    title: 'Caligo Radio',
    description: 'One live stream for every listener.',
    imagePath: null
  },
  thesaurarium: {
    kind: 'website',
    label: 'Thesaurarium',
    title: 'Thesaurarium',
    description: 'Films, series, episodes, actors, artists, albums and songs.',
    imagePath: null
  },
  'chat-rooms': {
    kind: 'website',
    label: 'Chat Rooms',
    title: 'Chat Rooms',
    description: 'Public chat rooms, called frequencies.',
    imagePath: null
  },
  omniscient: {
    kind: 'website',
    label: 'Omniscient AI',
    title: 'Omniscient AI',
    description: 'Ask about music, artists or lyrics.',
    imagePath: null
  }
};

const DAY = new Intl.DateTimeFormat('en-US', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' });

export const route = (requestPath: string): Route => {
  const path = requestPath.length > 1 ? requestPath.replace(/\/+$/, '') : requestPath;

  for (const detail of DETAILS) {
    const match = detail.pattern.exec(path);

    if (match) {
      const id = Number(match[1]);

      return { type: detail.type, id, path: `${detail.base}/${id}`, key: `${detail.type}-${id}` };
    }
  }

  const section = SECTIONS.find(candidate => candidate.pattern.test(path));

  return section
    ? { type: section.type, id: null, path: section.path, key: section.type }
    : { type: 'home', id: null, path: '/', key: 'home' };
};

const text = (value: unknown): string => (typeof value === 'string' ? value.trim() : '');

const list = (value: unknown): Record<string, unknown>[] =>
  Array.isArray(value) ? value.filter((item): item is Record<string, unknown> => typeof item === 'object' && item !== null) : [];

const names = (value: unknown): string => list(value).map(item => text(item.name)).filter(Boolean).join(', ');

const year = (value: unknown): string => text(value).slice(0, 4);

const joined = (...parts: string[]): string => parts.filter(Boolean).join(' · ');

const count = (amount: number, word: string): string => `${amount} ${word}${amount === 1 ? '' : 's'}`;

const sitePath = (value: unknown): string | null => {
  const uri = text(value);

  return uri.startsWith('/') && !uri.startsWith('//') ? uri : null;
};

const asRecord = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;

const titleLabel = (content: Record<string, unknown>): string => {
  const episode = asRecord(content.episode);
  const song = asRecord(content.song);

  switch (content.kind) {
    case 'movie':
      return joined('Film', year(content.releaseDate));
    case 'episode':
      return episode
        ? joined('Episode', text(episode.seriesTitle), `S${episode.seasonNumber} E${episode.episodeNumber}`)
        : 'Episode';
    case 'song':
      return joined('Song', song ? names(song.artists) : '', year(content.releaseDate));
    default:
      return joined('Title', year(content.releaseDate));
  }
};

const describeTitle = (content: Record<string, unknown>): Details => {
  const label = titleLabel(content);

  return {
    kind: 'website',
    label,
    title: text(content.title),
    description: text(content.description) || names(content.genres) || label,
    imagePath: sitePath(content.coverUri)
  };
};

const describeRelease = (release: Record<string, unknown>, word: string): Details => {
  const label = joined(word, names(release.artists), year(release.releaseDate));

  return {
    kind: 'music.album',
    label,
    title: text(release.title),
    description: text(release.description) || count(list(release.tracks).length, 'track'),
    imagePath: sitePath(release.coverUri)
  };
};

const DESCRIBE: Record<DetailType, { api: string; details: (data: Record<string, unknown>) => Details }> = {
  post: {
    api: '/api/blogposts',
    details: post => ({
      kind: 'article',
      label: joined(
        'Post',
        text(post.authorName).includes('@') ? '' : text(post.authorName),
        post.publishedDate ? DAY.format(new Date(text(post.publishedDate))) : ''),
      title: text(post.title),
      description: plainText(text(post.content)),
      imagePath: null
    })
  },
  title: { api: '/api/content', details: describeTitle },
  series: {
    api: '/api/series',
    details: series => ({
      kind: 'video.tv_show',
      label: joined('Series', year(series.releaseDate), count(list(series.seasons).length, 'season')),
      title: text(series.title),
      description: text(series.description),
      imagePath: null
    })
  },
  artist: {
    api: '/api/artists',
    details: artist => ({
      kind: 'profile',
      label: joined('Artist', text(artist.country)),
      title: text(artist.name),
      description:
        text(artist.biography) ||
        joined(count(list(artist.albums).length, 'album'), count(list(artist.songs).length, 'song')),
      imagePath: sitePath(artist.pictureUri) ?? list(artist.albums).map(album => sitePath(album.coverUri)).find(Boolean) ?? null
    })
  },
  album: { api: '/api/albums', details: album => describeRelease(album, 'Album') },
  single: { api: '/api/singles', details: single => describeRelease(single, 'Single') },
  actor: {
    api: '/api/actors',
    details: actor => ({
      kind: 'profile',
      label: joined('Actor', text(actor.nationality)),
      title: text(actor.name),
      description: text(actor.biography),
      imagePath: sitePath(actor.profilePictureUri)
    })
  }
};

export const describe = async (target: Route, api: Fetch): Promise<Page | null> => {
  if (target.id === null) {
    return { path: target.path, key: target.key, ...SECTION_PAGES[target.type as SectionType] };
  }

  const describer = DESCRIBE[target.type as DetailType];
  const data = asRecord(await api(`${describer.api}/${target.id}`));

  if (!data) {
    return null;
  }

  const details = describer.details(data);

  return details.title ? { path: target.path, key: target.key, ...details } : null;
};
