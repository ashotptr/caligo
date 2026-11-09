import { caligoApi } from '../store/api';
import type { ClapResult, ClapTally, PagedResult, PostDetail, PostSummary } from '../types';

export interface PostListArgs {
  mine?: true;
  contentId?: number;
}

export interface LoadedPost {
  post: PostDetail;
  etag: string;
}

export interface PostBody {
  title: string;
  content: string;
  tags: string[];
  publishAt?: string;
  contentId: number | null;
}

const ifMatch = (etag: string | null | undefined): Record<string, string> => (etag ? { 'If-Match': etag } : {});

export const postsApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getPosts: build.infiniteQuery<PagedResult<PostSummary>, PostListArgs, string | null>({
      infiniteQueryOptions: {
        initialPageParam: null,
        getNextPageParam: lastPage => lastPage.nextCursor ?? undefined
      },
      query: ({ queryArg, pageParam }) => ({
        url: '/api/blogposts',
        params: { ...queryArg, cursor: pageParam ?? undefined }
      }),
      providesTags: ['PostList']
    }),

    searchPosts: build.query<PostSummary[], string>({
      query: term => ({ url: '/api/blogposts/search', params: { query: term } }),
      providesTags: ['PostList']
    }),

    getPost: build.query<LoadedPost, string>({
      query: id => `/api/blogposts/${id}`,
      transformResponse: (post: PostDetail, meta) => ({
        post,
        etag: meta?.headers.etag ?? `"${post.version}"`
      }),
      providesTags: (_result, _error, id) => [{ type: 'Post', id }]
    }),

    createPost: build.mutation<PostDetail, PostBody>({
      query: body => ({ url: '/api/blogposts', method: 'POST', data: body }),
      invalidatesTags: (_result, error) => (error ? [] : ['PostList'])
    }),

    updatePost: build.mutation<void, { id: string; body: PostBody; etag: string | null }>({
      query: ({ id, body, etag }) => ({ url: `/api/blogposts/${id}`, method: 'PUT', data: body, headers: ifMatch(etag) }),
      invalidatesTags: (_result, error, { id }) => (error ? [] : [{ type: 'Post', id }, 'PostList'])
    }),

    deletePost: build.mutation<void, { id: number; etag?: string | null }>({
      query: ({ id, etag }) => ({ url: `/api/blogposts/${id}`, method: 'DELETE', headers: ifMatch(etag) }),
      invalidatesTags: (_result, error, { id }) => (error ? [] : [{ type: 'Post', id: String(id) }, 'PostList'])
    }),

    getClaps: build.query<ClapTally, number>({
      query: postId => `/api/blogposts/${postId}/claps`,
      providesTags: (_result, _error, postId) => [{ type: 'Claps', id: postId }]
    }),

    clapPost: build.mutation<ClapResult, { postId: number; count: number }>({
      query: ({ postId, count }) => ({ url: `/api/blogposts/${postId}/claps`, method: 'POST', data: { count } }),
      async onQueryStarted({ postId, count }, { dispatch, queryFulfilled }) {
        try {
          const { data } = await queryFulfilled;
          const refused = count - data.applied;

          if (refused > 0) {
            dispatch(postsApi.util.updateQueryData('getClaps', postId, tally => {
              tally.total -= refused;
              tally.mine -= refused;
            }));
          }
        } catch {
          dispatch(postsApi.util.updateQueryData('getClaps', postId, tally => {
            tally.total -= count;
            tally.mine -= count;
          }));
        }
      }
    })
  })
});

export const {
  useGetPostsInfiniteQuery,
  useSearchPostsQuery,
  useGetPostQuery,
  useLazyGetPostQuery,
  useCreatePostMutation,
  useUpdatePostMutation,
  useDeletePostMutation,
  useGetClapsQuery,
  useClapPostMutation
} = postsApi;
