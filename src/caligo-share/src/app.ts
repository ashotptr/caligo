import express, { type NextFunction, type Request, type Response } from 'express';
import type { Cards } from './cards.js';
import { metaHtml } from './meta.js';

const CARD_FILE = /^([a-z0-9-]+)\.png$/;

export const createApp = (cards: Cards, siteUrl: string) => {
  const app = express();

  app.disable('x-powered-by');

  app.get('/health', (_request, response) => {
    response.json({ status: 'ok' });
  });

  app.get('/share/cards/:file', async (request, response) => {
    const match = CARD_FILE.exec(request.params.file);
    const file = match ? await cards.imageFile(match[1]) : null;

    if (!file) {
      response.sendStatus(404);

      return;
    }

    response.set('Cache-Control', 'public, max-age=86400');
    response.sendFile(file);
  });

  app.get('/{*path}', async (request, response) => {
    const card = await cards.forPath(request.path);

    response.set('Cache-Control', 'public, max-age=600');
    response.type('html').send(metaHtml(card, siteUrl));
  });

  app.use((error: unknown, request: Request, response: Response, _next: NextFunction) => {
    console.error(`${request.method} ${request.path} failed:`, error);
    response.status(500).type('text').send('Something went wrong.');
  });

  return app;
};
