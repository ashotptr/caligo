import { describe as group, expect, it } from 'vitest';
import { describe, route } from '../src/pages.js';

const api = (answers: Record<string, unknown>) => async (path: string) => answers[path] ?? null;

group('route', () => {
  it.each([
    ['/posts/12', 'post-12', '/posts/12'],
    ['/posts/12/', 'post-12', '/posts/12'],
    ['/thesaurarium/titles/5', 'title-5', '/thesaurarium/titles/5'],
    ['/thesaurarium/albums/3', 'album-3', '/thesaurarium/albums/3'],
    ['/thesaurarium/singles/9', 'single-9', '/thesaurarium/singles/9'],
    ['/thesaurarium/artists/7', 'artist-7', '/thesaurarium/artists/7'],
    ['/thesaurarium/actors/40', 'actor-40', '/thesaurarium/actors/40'],
    ['/thesaurarium/series/2', 'series-2', '/thesaurarium/series/2'],
    ['/listen', 'radio', '/listen'],
    ['/thesaurarium/titles', 'thesaurarium', '/thesaurarium'],
    ['/chat-rooms', 'chat-rooms', '/chat-rooms'],
    ['/', 'home', '/'],
    ['/posts/new', 'home', '/'],
    ['/login', 'home', '/']
  ])('%s is the %s card', (requested, key, path) => {
    expect(route(requested)).toMatchObject({ key, path });
  });
});

group('describe', () => {
  it('turns a post into plain words with its author and date', async () => {
    const page = await describe(route('/posts/12'), api({
      '/api/blogposts/12': {
        title: 'Notes on fog',
        content: '# Heading\n\nSome **bold** words and a [link](https://example.test).',
        authorName: 'Ada',
        publishedDate: '2026-10-21T09:30:00Z'
      }
    }));

    expect(page).toMatchObject({
      kind: 'article',
      label: 'Post · Ada · October 21, 2026',
      title: 'Notes on fog',
      description: 'Heading Some bold words and a link.',
      imagePath: null
    });
  });

  it('never puts an e-mail address on a card', async () => {
    const page = await describe(route('/posts/3'), api({
      '/api/blogposts/3': { title: 'Hello', content: 'Hi.', authorName: 'someone@example.test', publishedDate: '2026-10-18T08:00:00Z' }
    }));

    expect(page?.label).toBe('Post · October 18, 2026');
  });

  it('labels a song with its artists and keeps only covers from the site', async () => {
    const page = await describe(route('/thesaurarium/titles/5'), api({
      '/api/content/5': {
        title: 'Low Tide',
        description: null,
        kind: 'song',
        releaseDate: '2024-03-01',
        genres: [{ id: 1, name: 'Ambient' }],
        coverUri: '/mixtape/low-tide/cover.jpg',
        song: { artists: [{ artistId: 1, name: 'Mira' }, { artistId: 2, name: 'Oro' }] }
      }
    }));

    expect(page).toMatchObject({
      label: 'Song · Mira, Oro · 2024',
      description: 'Ambient',
      imagePath: '/mixtape/low-tide/cover.jpg'
    });

    const outside = await describe(route('/thesaurarium/albums/3'), api({
      '/api/albums/3': { title: 'Elsewhere', artists: [], tracks: [{}, {}], coverUri: 'https://example.test/cover.jpg' }
    }));

    expect(outside).toMatchObject({ description: '2 tracks', imagePath: null });
  });

  it('labels an episode with its series, season and number', async () => {
    const page = await describe(route('/thesaurarium/titles/8'), api({
      '/api/content/8': {
        title: 'Pilot',
        description: 'It begins.',
        kind: 'episode',
        releaseDate: '2019-01-01',
        genres: [],
        coverUri: null,
        episode: { seriesTitle: 'Harbour', seasonNumber: 1, episodeNumber: 1 }
      }
    }));

    expect(page?.label).toBe('Episode · Harbour · S1 E1');
  });

  it('falls back to an album cover when an artist has no picture', async () => {
    const page = await describe(route('/thesaurarium/artists/7'), api({
      '/api/artists/7': {
        name: 'Mira',
        country: null,
        biography: null,
        pictureUri: null,
        albums: [{ coverUri: null }, { coverUri: '/mixtape/b/cover.png' }],
        songs: [{}]
      }
    }));

    expect(page).toMatchObject({ label: 'Artist', description: '2 albums · 1 song', imagePath: '/mixtape/b/cover.png' });
  });

  it('knows sections without asking the API, and nothing for a missing title', async () => {
    const asked: string[] = [];
    const tracking = async (path: string) => {
      asked.push(path);

      return null;
    };

    expect((await describe(route('/listen'), tracking))?.title).toBe('Caligo Radio');
    expect(await describe(route('/posts/404'), tracking)).toBeNull();
    expect(asked).toEqual(['/api/blogposts/404']);
  });
});
