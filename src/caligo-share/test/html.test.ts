import { describe, expect, it } from 'vitest';
import { cardHtml } from '../src/card.js';
import { metaHtml } from '../src/meta.js';
import type { Card } from '../src/store.js';
import { clip, plainText } from '../src/text.js';

const card: Card = {
  path: '/posts/12',
  key: 'post-12',
  kind: 'article',
  label: 'Post · Ada',
  title: 'Tom & "Jerry" <script>',
  description: 'A <b>bold</b> claim',
  imagePath: null,
  version: 'abc123',
  renderedVersion: null,
  checkedAt: new Date(0)
};

describe('the page for link crawlers', () => {
  it('carries the title, description, image and address, all escaped', () => {
    const html = metaHtml(card, 'https://caligo.example.test');

    expect(html).toContain('<meta property="og:title" content="Tom &amp; &quot;Jerry&quot; &lt;script&gt;">');
    expect(html).toContain('<meta property="og:description" content="A &lt;b&gt;bold&lt;/b&gt; claim">');
    expect(html).toContain('<meta property="og:image" content="https://caligo.example.test/share/cards/post-12.png?v=abc123">');
    expect(html).toContain('<meta property="og:url" content="https://caligo.example.test/posts/12">');
    expect(html).toContain('<meta name="twitter:card" content="summary_large_image">');
    expect(html).not.toContain('<script>');
  });
});

describe('the card image page', () => {
  it('escapes the text and shows the cover only when there is one', () => {
    expect(cardHtml(card, null)).toContain('Tom &amp; &quot;Jerry&quot; &lt;script&gt;');
    expect(cardHtml(card, null)).not.toContain('<img');
    expect(cardHtml(card, 'data:image/png;base64,AAAA')).toContain('<img class="cover" src="data:image/png;base64,AAAA" alt="">');
  });
});

describe('text helpers', () => {
  it('turns markdown into plain words', () => {
    expect(plainText('## Title\n\n- one\n- *two*\n\n```\ncode\n```\n![x](/y.png) [read](/z)')).toBe('Title one two read');
  });

  it('drops indented lists, tables and rules', () => {
    const markdown = '    Intro\n\n    - One\n    - Two\n\n    | A | B |\n    | --- | --- |\n    | 1 | 2 |\n\n    ---\n    End';

    expect(plainText(markdown)).toBe('Intro One Two A B 1 2 End');
  });

  it('cuts long text at a word and adds an ellipsis', () => {
    expect(clip('short', 10)).toBe('short');
    expect(clip('the quick brown fox jumps', 16)).toBe('the quick brown…');
  });
});
