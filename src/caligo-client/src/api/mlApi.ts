import { caligoApi } from '../store/api';
import type { AlbumAnalysis, FlaggedComment, MlMonitoring, SongAnalysis } from '../types';

export type CommentVerdict = 'spam' | 'toxic' | 'ok';

const camel = (key: string) => key.replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase());

export const camelize = (value: unknown): unknown => {
  if (Array.isArray(value)) {
    return value.map(camelize);
  }

  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, inner]) => [camel(key), camelize(inner)]));
  }

  return value;
};

const songsTag = (uri: string) => ({ type: 'Songs' as const, id: uri });
const albumTag = (folder: string) => ({ type: 'Songs' as const, id: `album:${folder}` });

export const mlApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getSongAnalysis: build.query<SongAnalysis, string>({
      query: uri => ({ url: '/api/ml/songs', params: { uri } }),
      transformResponse: response => camelize(response) as SongAnalysis,
      providesTags: (_result, _error, uri) => [songsTag(uri)]
    }),

    getAlbumAnalysis: build.query<AlbumAnalysis, string>({
      query: folder => ({ url: '/api/ml/albums', params: { folder } }),
      transformResponse: response => camelize(response) as AlbumAnalysis,
      providesTags: (_result, _error, folder) => [albumTag(folder)]
    }),

    getMlMonitoring: build.query<MlMonitoring, void>({
      query: () => '/api/insights/ml',
      transformResponse: response => camelize(response) as MlMonitoring,
      providesTags: ['MlMonitoring']
    }),

    getModerationQueue: build.query<FlaggedComment[], void>({
      query: () => '/api/moderation/queue',
      transformResponse: response => (camelize(response) as { comments: FlaggedComment[] }).comments,
      providesTags: ['Moderation']
    }),

    labelComment: build.mutation<void, { commentId: number; label: CommentVerdict }>({
      query: ({ commentId, label }) => ({ url: `/api/moderation/${commentId}/label`, method: 'POST', data: { label } }),
      async onQueryStarted({ commentId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(mlApi.util.updateQueryData('getModerationQueue', undefined, queue =>
          queue.filter(comment => comment.commentId !== commentId)));

        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
      invalidatesTags: (_result, error) => (error ? [] : ['MlMonitoring'])
    }),

    labelGenre: build.mutation<void, { uri: string; genre: string; folder: string | null }>({
      query: ({ uri, genre }) => ({ url: '/api/songs/genre', method: 'POST', data: { uri, genre } }),
      invalidatesTags: (_result, error, { uri, folder }) =>
        error ? [] : [songsTag(uri), ...(folder ? [albumTag(folder)] : []), 'MlMonitoring']
    })
  })
});

export const {
  useGetSongAnalysisQuery,
  useGetAlbumAnalysisQuery,
  useGetMlMonitoringQuery,
  useGetModerationQueueQuery,
  useLabelCommentMutation,
  useLabelGenreMutation
} = mlApi;
