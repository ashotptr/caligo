import { useEffect, useState } from 'react';
import type { TrendingSnapshot } from '../types';

/**
 * Subscribes to the live trending feed.
 *
 * The server pushes; nothing here polls. The API bridges the analytics service's gRPC server
 * stream to Server-Sent Events, because a browser cannot consume a gRPC stream directly - and
 * EventSource, unlike a WebSocket, reconnects on its own when the connection drops.
 */
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
        // A malformed frame is not worth tearing the stream down for.
      }
    });

    // Analytics is optional: the server says so explicitly rather than leaving the stream open
    // and silent, so the panel can hide itself instead of showing a permanent spinner.
    source.addEventListener('disabled', () => {
      setConnected(false);
      source.close();
    });

    source.onerror = () => setConnected(false);

    return () => source.close();
  }, [windowHours, limit]);

  return { snapshot, connected };
};
