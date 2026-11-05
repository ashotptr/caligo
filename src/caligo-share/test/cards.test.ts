import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createCards, RECHECK_MS } from '../src/cards.js';
import { handlePostEvent } from '../src/events.js';
import type { Card, CardStore } from '../src/store.js';

const memoryStore = (): CardStore & { rows: Map<string, Card> } => {
  const rows = new Map<string, Card>();

  return {
    rows,
    find: async cardPath => rows.get(cardPath) ?? null,
    findByKey: async key => [...rows.values()].find(card => card.key === key) ?? null,
    save: async card => {
      rows.set(card.path, { ...card });
    },
    markRendered: async (key, version) => {
      for (const card of rows.values()) {
        if (card.key === key && card.version === version) {
          card.renderedVersion = version;
        }
      }
    },
    remove: async cardPath => {
      rows.delete(cardPath);
    }
  };
};

describe('cards', () => {
  let dir: string;
  let clock: number;
  let title: string;
  let store: ReturnType<typeof memoryStore>;
  let api: ReturnType<typeof vi.fn>;
  let render: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(tmpdir(), 'caligo-share-'));
    clock = Date.parse('2026-11-02T12:00:00Z');
    title = 'First title';
    store = memoryStore();
    api = vi.fn(async (apiPath: string) =>
      apiPath === '/api/blogposts/12' ? { title, content: 'Body', authorName: 'Ada', publishedDate: '2026-11-01' } : null);
    render = vi.fn(async () => new Uint8Array([137, 80, 78, 71]));
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  const cards = () => createCards({ store, api, image: async () => null, render, dir, now: () => new Date(clock) });

  it('asks the API once a day and serves the saved card in between', async () => {
    const service = cards();

    await service.forPath('/posts/12');
    await service.forPath('/posts/12');

    expect(api).toHaveBeenCalledTimes(1);

    clock += RECHECK_MS;
    await service.forPath('/posts/12');

    expect(api).toHaveBeenCalledTimes(2);
  });

  it('draws each version once and draws again after a change', async () => {
    const service = cards();

    await service.forPath('/posts/12');
    const first = await service.imageFile('post-12');
    await service.imageFile('post-12');

    expect(render).toHaveBeenCalledTimes(1);

    title = 'Second title';
    await service.refresh('/posts/12');
    const second = await service.imageFile('post-12');

    expect(render).toHaveBeenCalledTimes(2);
    expect(second).not.toBe(first);
    expect(await readdir(dir)).toEqual([path.basename(second!)]);
  });

  it('falls back to the home card for a missing page and forgets deleted posts', async () => {
    const service = cards();

    expect((await service.forPath('/posts/99')).key).toBe('home');

    await service.refresh('/posts/12');
    expect(await readdir(dir)).toHaveLength(1);

    await service.remove('/posts/12');
    expect(store.rows.has('/posts/12')).toBe(false);
    expect(await readdir(dir)).toHaveLength(0);
  });

  it('follows post events and ignores everything else', async () => {
    const service = { forPath: vi.fn(), imageFile: vi.fn(), refresh: vi.fn(), remove: vi.fn() };

    await handlePostEvent(service, 'post.published', JSON.stringify({ postId: 12 }));
    await handlePostEvent(service, 'post.updated', JSON.stringify({ postId: 12 }));
    await handlePostEvent(service, 'post.deleted', JSON.stringify({ postId: 13 }));
    await handlePostEvent(service, 'post.viewed', JSON.stringify({ postId: 14 }));
    await handlePostEvent(service, 'post.published', null);

    expect(service.refresh.mock.calls).toEqual([['/posts/12'], ['/posts/12']]);
    expect(service.remove.mock.calls).toEqual([['/posts/13']]);
  });
});
