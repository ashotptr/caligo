import { caligoApi } from '../store/api';
import type {
  ActorDetail,
  ArtistDetail,
  ArtistSummary,
  ContentDetail,
  ContentKind,
  ContentSummary,
  InboxFile,
  MediaInfo,
  MusicImportResult,
  Named,
  PagedResult,
  ReleaseDetail,
  ReleaseSummary,
  SeriesDetail,
  SeriesSummary,
  SimilarContent
} from '../types';

export interface ContentListArgs {
  kind?: ContentKind;
  genreId?: string;
  query?: string;
}

export interface ReleaseListArgs {
  query?: string;
  artistId?: string;
}

export const catalogApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getGenres: build.query<Named[], void>({
      query: () => '/api/genres',
      keepUnusedDataFor: 3600
    }),

    getContentList: build.infiniteQuery<PagedResult<ContentSummary>, ContentListArgs, string | null>({
      infiniteQueryOptions: {
        initialPageParam: null,
        getNextPageParam: lastPage => lastPage.nextCursor ?? undefined
      },
      query: ({ queryArg, pageParam }) => ({
        url: '/api/content',
        params: { ...queryArg, cursor: pageParam ?? undefined }
      }),
      providesTags: ['ContentList']
    }),

    findContent: build.query<PagedResult<ContentSummary>, { query: string; limit: number }>({
      query: params => ({ url: '/api/content', params }),
      providesTags: ['ContentList']
    }),

    getContent: build.query<ContentDetail, number>({
      query: id => `/api/content/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'Content', id }]
    }),

    getSimilar: build.query<SimilarContent[], number>({
      query: id => `/api/content/${id}/similar`,
      providesTags: (_result, _error, id) => [{ type: 'Content', id }]
    }),

    getMedia: build.query<MediaInfo, number>({
      query: id => `/api/content/${id}/media`,
      providesTags: (_result, _error, id) => [{ type: 'Media', id }]
    }),

    getMediaInbox: build.query<InboxFile[], void>({
      query: () => '/api/media/inbox',
      providesTags: ['Inbox']
    }),

    queueMedia: build.mutation<MediaInfo, { contentId: number; sourceFile: string }>({
      query: ({ contentId, sourceFile }) => ({ url: `/api/content/${contentId}/media`, method: 'POST', data: { sourceFile } }),
      async onQueryStarted({ contentId }, { dispatch, queryFulfilled }) {
        try {
          const { data } = await queryFulfilled;

          dispatch(catalogApi.util.upsertQueryData('getMedia', contentId, data));
        } catch {
          return;
        }
      }
    }),

    setCastPayment: build.mutation<void, { contentId: number; actorId: number; payment: number | null }>({
      query: ({ contentId, actorId, payment }) => ({
        url: `/api/content/${contentId}/cast/${actorId}`,
        method: 'PUT',
        data: { payment }
      }),
      invalidatesTags: (_result, error, { contentId }) => (error ? [] : [{ type: 'Content', id: contentId }])
    }),

    getReleases: build.infiniteQuery<PagedResult<ReleaseSummary>, ReleaseListArgs, string | null>({
      infiniteQueryOptions: {
        initialPageParam: null,
        getNextPageParam: lastPage => lastPage.nextCursor ?? undefined
      },
      query: ({ queryArg, pageParam }) => ({
        url: '/api/releases',
        params: { ...queryArg, cursor: pageParam ?? undefined }
      }),
      providesTags: ['Releases']
    }),

    getReleaseArtists: build.query<Named[], void>({
      query: () => '/api/releases/artists',
      providesTags: ['Releases']
    }),

    getRelease: build.query<ReleaseDetail, { kind: 'album' | 'single'; id: string }>({
      query: ({ kind, id }) => `/api/${kind}s/${id}`,
      providesTags: ['Releases']
    }),

    getArtists: build.query<ArtistSummary[], string>({
      query: term => ({ url: '/api/artists', params: { query: term || undefined } }),
      providesTags: ['Artists']
    }),

    getArtist: build.query<ArtistDetail, string>({
      query: id => `/api/artists/${id}`,
      providesTags: ['Artists']
    }),

    getSeriesList: build.query<SeriesSummary[], void>({
      query: () => '/api/series',
      providesTags: ['Series']
    }),

    getSeries: build.query<SeriesDetail, string>({
      query: id => `/api/series/${id}`,
      providesTags: ['Series']
    }),

    getActor: build.query<ActorDetail, string>({
      query: id => `/api/actors/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'Actor', id }]
    }),

    lookupSong: build.query<{ contentId: number }, { artist: string; title: string }>({
      query: params => ({ url: '/api/songs/lookup', params }),
      providesTags: ['Releases']
    }),

    importMusic: build.mutation<MusicImportResult, void>({
      query: () => ({ url: '/api/music/import', method: 'POST' }),
      invalidatesTags: (_result, error) => (error ? [] : ['Releases', 'Artists', 'ContentList', 'Insights'])
    })
  })
});

export const {
  useGetGenresQuery,
  useGetContentListInfiniteQuery,
  useFindContentQuery,
  useGetContentQuery,
  useGetSimilarQuery,
  useGetMediaQuery,
  useGetMediaInboxQuery,
  useQueueMediaMutation,
  useSetCastPaymentMutation,
  useGetReleasesInfiniteQuery,
  useGetReleaseArtistsQuery,
  useGetReleaseQuery,
  useGetArtistsQuery,
  useGetArtistQuery,
  useGetSeriesListQuery,
  useGetSeriesQuery,
  useGetActorQuery,
  useLookupSongQuery,
  useImportMusicMutation
} = catalogApi;
