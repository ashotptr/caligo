import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { reader, renderWithStore } from '../test/render';
import { server } from '../test/server';
import ClapButton from './ClapButton';

const tally = (total: number, mine: number) => ({ total, clappers: 1, mine });

describe('ClapButton', () => {
  it('counts a clap at once and keeps it when the server accepts it', async () => {
    let sent = 0;

    server.use(
      http.get('/api/blogposts/9/claps', () => HttpResponse.json(tally(10, 0))),
      http.post('/api/blogposts/9/claps', async ({ request }) => {
        const { count } = await request.json() as { count: number };
        sent += count;
        return HttpResponse.json({ applied: count, mine: count, max: 50 });
      })
    );

    renderWithStore(<ClapButton postId={9} authorId="author-1" />, reader);

    await userEvent.click(await screen.findByRole('button', { name: 'Clap' }));

    expect(screen.getByText('11 claps')).toBeInTheDocument();
    expect(screen.getByText('yours: 1 / 50')).toBeInTheDocument();

    await waitFor(() => expect(sent).toBe(1), { timeout: 2000 });
    expect(screen.getByText('11 claps')).toBeInTheDocument();
  });

  it('takes back the claps the server refused', async () => {
    server.use(
      http.get('/api/blogposts/9/claps', () => HttpResponse.json(tally(10, 49))),
      http.post('/api/blogposts/9/claps', () => HttpResponse.json({ applied: 0, mine: 49, max: 50 }))
    );

    renderWithStore(<ClapButton postId={9} authorId="author-1" />, reader);

    await userEvent.click(await screen.findByRole('button', { name: 'Clap' }));
    expect(screen.getByText('11 claps')).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText('10 claps')).toBeInTheDocument(), { timeout: 2000 });
    expect(screen.getByText('yours: 49 / 50')).toBeInTheDocument();
  });

  it('rolls the claps back and says why when sending fails', async () => {
    server.use(
      http.get('/api/blogposts/9/claps', () => HttpResponse.json(tally(10, 0))),
      http.post('/api/blogposts/9/claps', () => new HttpResponse('Claps are paused.', { status: 503 }))
    );

    renderWithStore(<ClapButton postId={9} authorId="author-1" />, reader);

    await userEvent.click(await screen.findByRole('button', { name: 'Clap' }));
    expect(screen.getByText('11 claps')).toBeInTheDocument();

    expect(await screen.findByText('Claps are paused.', {}, { timeout: 2000 })).toBeInTheDocument();
    expect(screen.getByText('10 claps')).toBeInTheDocument();
  });

  it('shows no clap button to the author', async () => {
    server.use(http.get('/api/blogposts/9/claps', () => HttpResponse.json(tally(3, 0))));

    renderWithStore(<ClapButton postId={9} authorId={reader.id} />, reader);

    expect(await screen.findByText('3 claps')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clap' })).not.toBeInTheDocument();
  });
});
