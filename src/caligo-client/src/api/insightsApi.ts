import { caligoApi } from '../store/api';
import type {
  ActiveUser,
  EngagingContent,
  LabRunDetail,
  LabRunsResponse,
  PopularContent,
  QueryResult,
  SavedQuery
} from '../types';

export const insightsApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getPopular: build.query<PopularContent[], void>({
      query: () => '/api/insights/popular',
      providesTags: ['Insights']
    }),

    getEngaging: build.query<EngagingContent[], void>({
      query: () => '/api/insights/engaging',
      providesTags: ['Insights']
    }),

    getActiveUsers: build.query<ActiveUser[], void>({
      query: () => '/api/insights/active-users',
      providesTags: ['Insights']
    }),

    getSavedQueries: build.query<SavedQuery[], void>({
      query: () => '/api/insights/queries'
    }),

    runSavedQuery: build.query<QueryResult, number>({
      query: number => `/api/insights/queries/${number}`,
      providesTags: ['Insights']
    }),

    getLabRuns: build.query<LabRunsResponse, void>({
      query: () => '/api/lab/runs',
      providesTags: ['Lab']
    }),

    getLabRun: build.query<LabRunDetail, string>({
      query: runId => `/api/lab/runs/${runId}`,
      providesTags: ['Lab']
    })
  })
});

export const {
  useGetPopularQuery,
  useGetEngagingQuery,
  useGetActiveUsersQuery,
  useGetSavedQueriesQuery,
  useLazyRunSavedQueryQuery,
  useGetLabRunsQuery,
  useGetLabRunQuery
} = insightsApi;
