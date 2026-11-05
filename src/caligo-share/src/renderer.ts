import puppeteer from 'puppeteer-core';
import { CARD_HEIGHT, CARD_WIDTH } from './card.js';

export type Render = (html: string) => Promise<Uint8Array>;

export const createRenderer = (chromePath: string): Render => {
  let queue: Promise<unknown> = Promise.resolve();

  const renderNow = async (html: string): Promise<Uint8Array> => {
    const browser = await puppeteer.launch({
      executablePath: chromePath,
      headless: true,
      args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu']
    });

    try {
      const page = await browser.newPage();

      await page.setViewport({ width: CARD_WIDTH, height: CARD_HEIGHT });
      await page.setRequestInterception(true);

      page.on('request', request => {
        if (request.url().startsWith('data:')) {
          void request.continue();
        } else {
          void request.abort();
        }
      });

      await page.setContent(html, { waitUntil: 'load' });

      return await page.screenshot({ type: 'png' });
    } finally {
      await browser.close();
    }
  };

  return html => {
    const rendered = queue.then(() => renderNow(html));

    queue = rendered.catch(() => undefined);

    return rendered;
  };
};
