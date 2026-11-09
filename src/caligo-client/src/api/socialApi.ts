import { caligoApi } from '../store/api';
import type { ContentComment, Interaction } from '../types';

export const COMMENTS_PAGE = 20;

const afterActivity = (contentId: number) => [
  { type: 'Content' as const, id: contentId },
  'History' as const,
  'Recommendations' as const,
  'Notifications' as const,
  'Insights' as const
];

export const socialApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getComments: build.infiniteQuery<ContentComment[], number, number | null>({
      infiniteQueryOptions: {
        initialPageParam: null,
        getNextPageParam: lastPage =>
          lastPage.length === COMMENTS_PAGE ? lastPage[lastPage.length - 1].commentId : undefined
      },
      query: ({ queryArg, pageParam }) => ({
        url: `/api/content/${queryArg}/comments`,
        params: { limit: COMMENTS_PAGE, before: pageParam ?? undefined }
      }),
      providesTags: (_result, _error, contentId) => [{ type: 'Comments', id: contentId }]
    }),

    addComment: build.mutation<ContentComment, { contentId: number; comment: string }>({
      query: ({ contentId, comment }) => ({ url: `/api/content/${contentId}/comments`, method: 'POST', data: { comment } }),
      invalidatesTags: (_result, error, { contentId }) =>
        (error ? [] : [{ type: 'Comments', id: contentId }, { type: 'Content', id: contentId }, 'Insights'])
    }),

    getInteraction: build.query<Interaction, number>({
      query: contentId => `/api/content/${contentId}/interaction`,
      providesTags: (_result, _error, contentId) => [{ type: 'Interaction', id: contentId }]
    }),

    saveInteraction: build.mutation<Interaction, { contentId: number; liked: boolean | null; rating: number | null }>({
      query: ({ contentId, liked, rating }) => ({
        url: `/api/content/${contentId}/interaction`,
        method: 'PUT',
        data: { liked, rating }
      }),
      invalidatesTags: (_result, error, { contentId }) =>
        (error ? [] : [{ type: 'Interaction', id: contentId }, ...afterActivity(contentId)])
    }),

    removeInteraction: build.mutation<void, number>({
      query: contentId => ({ url: `/api/content/${contentId}/interaction`, method: 'DELETE' }),
      invalidatesTags: (_result, error, contentId) =>
        (error ? [] : [{ type: 'Interaction', id: contentId }, ...afterActivity(contentId)])
    }),

    logWatch: build.mutation<void, { contentId: number; timestamp: number | null }>({
      query: ({ contentId, timestamp }) => ({ url: `/api/content/${contentId}/watch`, method: 'POST', data: { timestamp } }),
      invalidatesTags: (_result, error, { contentId }) => (error ? [] : afterActivity(contentId))
    })
  })
});

export const {
  useGetCommentsInfiniteQuery,
  useAddCommentMutation,
  useGetInteractionQuery,
  useSaveInteractionMutation,
  useRemoveInteractionMutation,
  useLogWatchMutation
} = socialApi;
