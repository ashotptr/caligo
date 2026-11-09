import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { mixtapeFolder, soundWords } from '../ml';
import { makeStore } from '../store/store';
import { server } from '../test/server';
import type { SongAnalysis } from '../types';
import { mlApi } from './mlApi';

const song = (changes: Partial<SongAnalysis> = {}): SongAnalysis => ({
  path: 'mixtape/am/do-i-wanna-know.mp3',
  uri: '/mixtape/am/do-i-wanna-know.mp3',
  title: 'Do I Wanna Know?',
  artist: 'Arctic Monkeys',
  album: 'AM',
  genre: 'Rock',
  genres: null,
  genreLabel: null,
  energy: 0.5,
  valence: 0.5,
  danceability: 0.5,
  instrumental: 0.2,
  soundGroup: 1,
  ...changes
});

describe('mlApi', () => {
  it('reads the snake_case album answer into the client shape', async () => {
    server.use(http.get('/api/ml/albums', ({ request }) => HttpResponse.json({
      folder: new URL(request.url).searchParams.get('folder'),
      colors: [{ color: '#c81e1e', share: 0.6 }],
      accent: '#d65c5c',
      songs: [{ path: 'mixtape/am/a.mp3', uri: '/mixtape/am/a.mp3', genre_label: 'Pop', sound_group: 2 }]
    })));

    const store = makeStore();
    const { data } = await store.dispatch(mlApi.endpoints.getAlbumAnalysis.initiate('am'));

    expect(data?.folder).toBe('am');
    expect(data?.accent).toBe('#d65c5c');
    expect(data?.songs[0].genreLabel).toBe('Pop');
    expect(data?.songs[0].soundGroup).toBe(2);
  });

  it('takes a decided comment out of the queue at once and puts it back if saving fails', async () => {
    server.use(
      http.get('/api/moderation/queue', () => HttpResponse.json({
        comments: [{ comment_id: 4, content_id: 2, text: 'subscribe', scored_at: '2026-11-07', spam: 0.9, toxicity: null, reasons: [] }]
      })),
      http.post('/api/moderation/4/label', () => new HttpResponse('Unavailable', { status: 503 }))
    );

    const store = makeStore();
    await store.dispatch(mlApi.endpoints.getModerationQueue.initiate());
    const saving = store.dispatch(mlApi.endpoints.labelComment.initiate({ commentId: 4, label: 'spam' }));
    const queue = () => mlApi.endpoints.getModerationQueue.select()(store.getState()).data;

    expect(queue()).toEqual([]);
    await saving;
    expect(queue()?.map(comment => comment.commentId)).toEqual([4]);
  });
});

describe('sound words', () => {
  it('names the genre, the mood and whether there is singing', () => {
    expect(soundWords(song({ energy: 0.8, valence: 0.2, danceability: 0.7 }))).toEqual(
      ['Rock', 'energetic', 'dark', 'danceable', 'vocal']);
    expect(soundWords(song({ genreLabel: 'Folk', energy: 0.1, valence: 0.9, instrumental: 0.9 }))).toEqual(
      ['Folk', 'calm', 'bright', 'instrumental']);
    expect(soundWords(song({ genre: 'Instrumental', instrumental: 0.95 }))).toEqual(['Instrumental']);
  });

  it('says nothing about singing when the model is unsure', () => {
    expect(soundWords(song({ energy: 0.5, valence: 0.5, danceability: 0.5, instrumental: 0.54 }))).toEqual(['Rock']);
  });

  it('finds the album folder in a mixtape address only', () => {
    expect(mixtapeFolder('/mixtape/nirvana%20unplugged/cover.jpg')).toBe('nirvana unplugged');
    expect(mixtapeFolder('/music/song.mp3')).toBeNull();
    expect(mixtapeFolder(null)).toBeNull();
    expect(mixtapeFolder('/mixtape/bad%zz/a.mp3')).toBeNull();
  });
});
