import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { reader, renderWithStore } from '../test/render';
import { server } from '../test/server';
import type { ContentComment } from '../types';
import CommentsSection from './CommentsSection';

const comment = (commentId: number, text: string): ContentComment => ({
  commentId,
  contentId: 4,
  author: 'listener',
  comment: text
});

describe('CommentsSection', () => {
  it('refreshes the list after a comment is posted', async () => {
    const stored = [comment(1, 'First!')];

    server.use(
      http.get('/api/content/4/comments', () => HttpResponse.json([...stored].reverse())),
      http.post('/api/content/4/comments', async ({ request }) => {
        const { comment: text } = await request.json() as { comment: string };
        const created = comment(stored.length + 1, text);
        stored.push(created);
        return HttpResponse.json(created);
      })
    );

    renderWithStore(<CommentsSection contentId={4} />, reader);

    expect(await screen.findByText('First!')).toBeInTheDocument();

    await userEvent.type(screen.getByPlaceholderText('Say something about it…'), 'Great soundtrack');
    await userEvent.click(screen.getByRole('button', { name: 'Post' }));

    expect(await screen.findByText('Great soundtrack')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Say something about it…')).toHaveValue('');
  });

  it('asks for a profile when the server says one is needed', async () => {
    server.use(
      http.get('/api/content/4/comments', () => HttpResponse.json([])),
      http.post('/api/content/4/comments', () => new HttpResponse('Set up your profile first.', { status: 409 }))
    );

    renderWithStore(<CommentsSection contentId={4} />, reader);

    await userEvent.type(await screen.findByPlaceholderText('Say something about it…'), 'Hello');
    await userEvent.click(screen.getByRole('button', { name: 'Post' }));

    expect(await screen.findByText(/Comments need a profile/)).toBeInTheDocument();
  });
});
