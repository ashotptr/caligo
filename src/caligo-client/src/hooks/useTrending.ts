import { useGetTrendingQuery } from '../api/analyticsApi';

export const useTrending = (windowHours = 24, limit = 5) => {
  const { data } = useGetTrendingQuery({ windowHours, limit });

  return { snapshot: data?.snapshot ?? null, connected: data?.connected ?? false };
};
