import { caligoApi } from '../store/api';
import type { RadioHistory } from '../types';

export interface LiveNow {
  listeners?: number;
}

export const radioApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getRadioRecent: build.query<RadioHistory, number>({
      query: limit => ({ url: '/api/radio/recent', params: { limit } }),
      providesTags: ['Radio']
    }),

    getLiveNow: build.query<LiveNow, void>({
      query: () => '/live/now'
    })
  })
});

export const { useGetRadioRecentQuery, useGetLiveNowQuery } = radioApi;
