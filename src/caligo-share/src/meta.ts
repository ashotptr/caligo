import { CARD_HEIGHT, CARD_WIDTH } from './card.js';
import type { Card } from './store.js';
import { clip, escapeHtml } from './text.js';

export const imageUrl = (card: Pick<Card, 'key' | 'version'>, siteUrl: string): string =>
  `${siteUrl}/share/cards/${card.key}.png?v=${card.version}`;

export const metaHtml = (card: Card, siteUrl: string): string => {
  const url = escapeHtml(`${siteUrl}${card.path}`);
  const image = escapeHtml(imageUrl(card, siteUrl));
  const title = escapeHtml(card.title);
  const description = escapeHtml(clip(card.description, 200));
  const heading = card.key === 'home' ? title : `${title} · Caligo`;

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>${heading}</title>
<meta name="description" content="${description}">
<link rel="canonical" href="${url}">
<meta property="og:site_name" content="Caligo">
<meta property="og:type" content="${escapeHtml(card.kind)}">
<meta property="og:url" content="${url}">
<meta property="og:title" content="${title}">
<meta property="og:description" content="${description}">
<meta property="og:image" content="${image}">
<meta property="og:image:width" content="${CARD_WIDTH}">
<meta property="og:image:height" content="${CARD_HEIGHT}">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="${title}">
<meta name="twitter:description" content="${description}">
<meta name="twitter:image" content="${image}">
</head>
<body>
<h1>${title}</h1>
<p>${description}</p>
<p><a href="${url}">Open on Caligo</a></p>
</body>
</html>`;
};
