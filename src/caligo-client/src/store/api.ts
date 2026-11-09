import { createApi } from '@reduxjs/toolkit/query/react';
import { axiosBaseQuery } from './baseQuery';

export const caligoApi = createApi({
  reducerPath: 'caligoApi',
  baseQuery: axiosBaseQuery,
  tagTypes: [
    'Post',
    'PostList',
    'Claps',
    'PostStats',
    'Content',
    'ContentList',
    'Media',
    'Inbox',
    'Comments',
    'Interaction',
    'Releases',
    'Artists',
    'Series',
    'Actor',
    'Collections',
    'Collection',
    'Profile',
    'Spending',
    'History',
    'Recommendations',
    'Notifications',
    'Insights',
    'Lab',
    'Radio',
    'Songs',
    'Moderation',
    'MlMonitoring'
  ],
  endpoints: () => ({})
});
