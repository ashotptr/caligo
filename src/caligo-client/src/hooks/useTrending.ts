import { useEffect, useState } from 'react';
import type { TrendingSnapshot } from '../types';

export const useTrending = (windowHours = 24, limit = 5) => {
  const [snapshot, setSnapshot] = useState<TrendingSnapshot | null>(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const source = new EventSource(
      `/api/analytics/trending/stream?windowHours=${windowHours}&limit=${limit}`
    );

    source.addEventListener('trending', event => {
      setConnected(true);
      try {
        setSnapshot(JSON.parse((event as MessageEvent).data) as TrendingSnapshot);
      } catch {
      }
    });

    source.addEventListener('disabled', () => {
      setConnected(false);
      source.close();
    });

    source.onerror = () => setConnected(false);

    return () => source.close();
  }, [windowHours, limit]);

  return { snapshot, connected };
};
