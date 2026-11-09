import { caligoApi } from '../store/api';
import type { Plan, Recommendation, Spending, StreamingNotification, StreamingProfile, WatchEntry } from '../types';

export interface NewProfile {
  firstName: string;
  lastName: string;
  dateOfBirth: string;
  gender: string;
}

export const profileApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getStreamingProfile: build.query<StreamingProfile, void>({
      query: () => '/api/me/streaming-profile',
      providesTags: ['Profile']
    }),

    createStreamingProfile: build.mutation<StreamingProfile, NewProfile>({
      query: body => ({ url: '/api/me/streaming-profile', method: 'POST', data: body }),
      invalidatesTags: (_result, error) =>
        (error ? [] : ['Profile', 'Spending', 'History', 'Recommendations', 'Notifications', 'Interaction'])
    }),

    getPlans: build.query<Plan[], void>({
      query: () => '/api/subscription/plans',
      keepUnusedDataFor: 3600
    }),

    getSpending: build.query<Spending, void>({
      query: () => '/api/me/spending',
      providesTags: ['Spending']
    }),

    getHistory: build.query<WatchEntry[], void>({
      query: () => '/api/me/history',
      providesTags: ['History']
    }),

    getRecommendations: build.query<Recommendation[], void>({
      query: () => '/api/me/recommendations',
      providesTags: ['Recommendations']
    }),

    getNotifications: build.query<StreamingNotification[], void>({
      query: () => '/api/me/notifications',
      providesTags: ['Notifications']
    })
  })
});

export const {
  useGetStreamingProfileQuery,
  useCreateStreamingProfileMutation,
  useGetPlansQuery,
  useGetSpendingQuery,
  useGetHistoryQuery,
  useGetRecommendationsQuery,
  useGetNotificationsQuery
} = profileApi;
