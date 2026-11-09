import { caligoApi } from '../store/api';
import type {
  FunnelResponse,
  PlaybackQuality,
  PostStats,
  PostStatsResponse,
  SeriesBucket,
  SeriesResponse,
  TopWindowResponse,
  TrendingSnapshot
} from '../types';

export interface TrendingState {
  snapshot: TrendingSnapshot | null;
  connected: boolean;
}

export const analyticsApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getPostStatsBatch: build.query<PostStatsResponse, number[]>({
      query: ids => ({ url: '/api/analytics/posts', params: { ids: ids.join(',') } })
    }),

    getPostStats: build.query<PostStats & { enabled: boolean }, string>({
      query: id => `/api/analytics/posts/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'PostStats', id }]
    }),

    getPostSeries: build.query<SeriesResponse, { id: string; bucket: SeriesBucket }>({
      query: ({ id, bucket }) => ({ url: `/api/analytics/posts/${id}/series`, params: { bucket } })
    }),

    getPostFunnel: build.query<FunnelResponse, { id: string; days: number }>({
      query: ({ id, days }) => ({ url: `/api/analytics/posts/${id}/funnel`, params: { days } })
    }),

    getTopWindow: build.query<TopWindowResponse, { minutes: number; by: 'views' | 'claps'; limit: number }>({
      query: params => ({ url: '/api/analytics/top/window', params })
    }),

    getPlaybackQuality: build.query<PlaybackQuality, number>({
      query: days => ({ url: '/api/analytics/playback', params: { days } }),
      providesTags: ['Insights']
    }),

    getTrending: build.query<TrendingState, { windowHours: number; limit: number }>({
      queryFn: () => ({ data: { snapshot: null, connected: false } }),
      keepUnusedDataFor: 0,
      async onCacheEntryAdded({ windowHours, limit }, { updateCachedData, cacheDataLoaded, cacheEntryRemoved }) {
        await cacheDataLoaded;

        const source = new EventSource(`/api/analytics/trending/stream?windowHours=${windowHours}&limit=${limit}`);

        source.addEventListener('trending', event => {
          try {
            const snapshot = JSON.parse((event as MessageEvent).data) as TrendingSnapshot;

            updateCachedData(state => {
              state.snapshot = snapshot;
              state.connected = true;
            });
          } catch {
            return;
          }
        });

        source.addEventListener('disabled', () => {
          updateCachedData(state => {
            state.connected = false;
          });
          source.close();
        });

        source.onerror = () => updateCachedData(state => {
          state.connected = false;
        });

        await cacheEntryRemoved;

        source.close();
      }
    })
  })
});

export const {
  useGetPostStatsBatchQuery,
  useGetPostStatsQuery,
  useGetPostSeriesQuery,
  useGetPostFunnelQuery,
  useGetTopWindowQuery,
  useGetPlaybackQualityQuery,
  useGetTrendingQuery
} = analyticsApi;
