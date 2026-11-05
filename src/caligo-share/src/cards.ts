import { createHash } from 'node:crypto';
import { access, mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { cardHtml } from './card.js';
import { describe, route, type Fetch, type Page } from './pages.js';
import type { Render } from './renderer.js';
import type { Card, CardStore } from './store.js';

export const RECHECK_MS = 24 * 60 * 60 * 1000;

export interface Cards {
  forPath(requestPath: string): Promise<Card>;
  imageFile(key: string): Promise<string | null>;
  refresh(requestPath: string): Promise<void>;
  remove(requestPath: string): Promise<void>;
}

export interface CardsOptions {
  store: CardStore;
  api: Fetch;
  image: (imagePath: string) => Promise<string | null>;
  render: Render;
  dir: string;
  now?: () => Date;
}

export const versionOf = (page: Page): string =>
  createHash('sha256')
    .update(JSON.stringify([page.kind, page.label, page.title, page.description, page.imagePath]))
    .digest('hex')
    .slice(0, 12);

const exists = async (file: string): Promise<boolean> => {
  try {
    await access(file);

    return true;
  } catch {
    return false;
  }
};

export const createCards = ({ store, api, image, render, dir, now = () => new Date() }: CardsOptions): Cards => {
  const rendering = new Map<string, Promise<string>>();

  const fileOf = (card: Pick<Card, 'key' | 'version'>): string => path.join(dir, `${card.key}-${card.version}.png`);

  const forget = async (card: Card): Promise<void> => {
    await rm(fileOf(card), { force: true });
    await store.remove(card.path);
  };

  const load = async (requestPath: string, force: boolean): Promise<Card | null> => {
    const target = route(requestPath);
    const cached = await store.find(target.path);

    if (cached && !force && now().getTime() - cached.checkedAt.getTime() < RECHECK_MS) {
      return cached;
    }

    let page: Page | null;

    try {
      page = await describe(target, api);
    } catch (error) {
      if (cached) {
        return cached;
      }

      throw error;
    }

    if (!page) {
      if (cached) {
        await forget(cached);
      }

      return null;
    }

    const version = versionOf(page);

    if (cached && cached.version !== version) {
      await rm(fileOf(cached), { force: true });
    }

    const card: Card = {
      ...page,
      version,
      renderedVersion: cached?.version === version ? cached.renderedVersion : null,
      checkedAt: now()
    };

    await store.save(card);

    return card;
  };

  const draw = async (card: Card): Promise<string> => {
    const file = fileOf(card);
    const picture = card.imagePath ? await image(card.imagePath) : null;
    const png = await render(cardHtml(card, picture));

    await mkdir(dir, { recursive: true });
    await writeFile(file, png);
    await store.markRendered(card.key, card.version);

    return file;
  };

  const imageFile = async (key: string): Promise<string | null> => {
    const card = await store.findByKey(key);

    if (!card) {
      return null;
    }

    const file = fileOf(card);

    if (card.renderedVersion === card.version && (await exists(file))) {
      return file;
    }

    const pending = rendering.get(file) ?? draw(card).finally(() => rendering.delete(file));

    rendering.set(file, pending);

    return pending;
  };

  return {
    forPath: async requestPath => {
      try {
        const card = await load(requestPath, false);

        if (card) {
          return card;
        }
      } catch (error) {
        console.warn(`Could not describe ${requestPath}:`, error);
      }

      return (await load('/', false))!;
    },

    imageFile,

    refresh: async requestPath => {
      const card = await load(requestPath, true);

      if (card) {
        await imageFile(card.key);
      }
    },

    remove: async requestPath => {
      const cached = await store.find(route(requestPath).path);

      if (cached) {
        await forget(cached);
      }
    }
  };
};
