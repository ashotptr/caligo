import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { makeStore } from '../store/store';
import { server } from '../test/server';
import type { PagedResult, PostDetail, PostSummary } from '../types';
import { postsApi } from './postsApi';

const post: PostDetail = {
  id: 5,
  title: 'Hello',
  content: 'Body',
  publishedDate: '2026-11-01T10:00:00',
  authorName: 'ashot',
  authorId: 'author-1',
  tags: [],
  status: 'published',
  scheduledFor: null,
  version: 3,
  contentId: null,
  contentTitle: null
};

const page = (items: PostSummary[]): PagedResult<PostSummary> => ({ items, nextCursor: null, hasMore: false });

describe('postsApi', () => {
  it('returns a post together with the ETag the server sent', async () => {
    server.use(http.get('/api/blogposts/5', () => HttpResponse.json(post, { headers: { ETag: '"7"' } })));

    const store = makeStore();
    const { data } = await store.dispatch(postsApi.endpoints.getPost.initiate('5'));

    expect(data?.post.title).toBe('Hello');
    expect(data?.etag).toBe('"7"');
  });

  it('falls back to the version when the server sends no ETag', async () => {
    server.use(http.get('/api/blogposts/5', () => HttpResponse.json(post)));

    const store = makeStore();
    const { data } = await store.dispatch(postsApi.endpoints.getPost.initiate('5'));

    expect(data?.etag).toBe('"3"');
  });

  it('refetches the post list after a post is deleted', async () => {
    let listed = 0;

    server.use(
      http.get('/api/blogposts', () => {
        listed += 1;
        return HttpResponse.json(page([]));
      }),
      http.delete('/api/blogposts/5', () => new HttpResponse(null, { status: 204 }))
    );

    const store = makeStore();
    const subscription = store.dispatch(postsApi.endpoints.getPosts.initiate({}));

    await subscription;
    expect(listed).toBe(1);

    await store.dispatch(postsApi.endpoints.deletePost.initiate({ id: 5 })).unwrap();
    await expect.poll(() => listed).toBe(2);

    subscription.unsubscribe();
  });

  it('keeps the post list when a delete fails', async () => {
    let listed = 0;

    server.use(
      http.get('/api/blogposts', () => {
        listed += 1;
        return HttpResponse.json(page([]));
      }),
      http.delete('/api/blogposts/5', () => new HttpResponse('gone', { status: 412 }))
    );

    const store = makeStore();
    const subscription = store.dispatch(postsApi.endpoints.getPosts.initiate({}));

    await subscription;

    await expect(store.dispatch(postsApi.endpoints.deletePost.initiate({ id: 5, etag: '"1"' })).unwrap())
      .rejects.toMatchObject({ status: 412 });
    expect(listed).toBe(1);

    subscription.unsubscribe();
  });
});
