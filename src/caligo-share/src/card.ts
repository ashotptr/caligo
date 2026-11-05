import type { Page } from './pages.js';
import { clip, escapeHtml } from './text.js';

export const CARD_WIDTH = 1200;
export const CARD_HEIGHT = 630;

export const cardHtml = (page: Pick<Page, 'label' | 'title' | 'description'>, image: string | null): string => `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<style>
* { box-sizing: border-box; margin: 0; }
body { width: ${CARD_WIDTH}px; height: ${CARD_HEIGHT}px; overflow: hidden; background: #0a0a0a; color: #e0e0e0; font-family: 'Liberation Sans', Arial, Helvetica, sans-serif; }
.card { position: relative; display: flex; align-items: center; gap: 56px; width: 100%; height: 100%; padding: 72px; background: radial-gradient(ellipse at 15% 0%, rgba(255, 255, 255, 0.07), transparent 55%), radial-gradient(ellipse at 100% 100%, rgba(255, 255, 255, 0.04), transparent 50%); }
.cover { flex: none; width: 420px; height: 420px; object-fit: cover; border: 1px solid #333333; }
.text { display: flex; flex-direction: column; gap: 22px; min-width: 0; }
.label { font-family: 'Liberation Mono', 'Courier New', monospace; font-size: 24px; letter-spacing: 0.08em; text-transform: uppercase; color: #808080; }
.title { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3; overflow: hidden; font-size: 64px; font-weight: 700; line-height: 1.1; color: #ffffff; }
.description { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3; overflow: hidden; font-size: 28px; line-height: 1.4; color: #808080; }
.brand { position: absolute; top: 36px; right: 72px; font-family: 'Liberation Mono', 'Courier New', monospace; font-size: 26px; color: #555555; }
</style>
</head>
<body>
<div class="card">
${image ? `<img class="cover" src="${escapeHtml(image)}" alt="">` : ''}
<div class="text">
<div class="label">${escapeHtml(page.label)}</div>
<div class="title">${escapeHtml(page.title)}</div>
<div class="description">${escapeHtml(clip(page.description, 240))}</div>
</div>
<div class="brand">./caligo</div>
</div>
</body>
</html>`;
