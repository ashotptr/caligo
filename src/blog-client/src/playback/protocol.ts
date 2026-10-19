import api from '../api/axios';

export interface SubtitleTrackInfo {
  label: string;
  language: string | null;
  url: string;
}

export interface RenditionInfo {
  kind: 'video' | 'audio' | 'progressive';
  codec: string;
  bitrate: number;
  width: number | null;
  height: number | null;
  language: string | null;
}

export interface PlaybackSession {
  session_id: string;
  content_id: number;
  expires_at: string;
  lease_seconds: number;
  heartbeat_seconds: number;
  position: number;
  duration_seconds: number | null;
  playlist_url: string | null;
  manifest_url: string | null;
  progressive_url: string | null;
  subtitles: SubtitleTrackInfo[];
  renditions: RenditionInfo[];
  max_streams: number;
  resumed: boolean;
}

export interface PositionReply {
  position: number;
  seq: number;
  expires_at: string;
  lease_seconds: number;
}

export interface CompletionReply {
  completion_id: string;
  content_id: number;
  watched_content_id: number | null;
  completed_at: string;
  repeated: boolean;
}

export interface PlaybackEvent {
  event_id: string;
  seq: number;
  type: 'startup' | 'stall' | 'quality' | 'error' | 'play' | 'pause' | 'seek' | 'ended';
  at: number;
  position?: number;
  duration_ms?: number;
  from?: number;
  to?: number;
  delivery?: string;
  detail?: string;
}

export const startSession = async (contentId: number, device: string): Promise<PlaybackSession> =>
  (await api.post<PlaybackSession>('/api/v1/sessions', { content_id: contentId, device_id: device })).data;

export const reportPosition = async (sessionId: string, position: number, seq: number): Promise<PositionReply> =>
  (await api.put<PositionReply>(`/api/v1/sessions/${sessionId}/position`, { position, seq })).data;

export const completeSession = async (sessionId: string, completionId: string, position: number | null): Promise<CompletionReply> =>
  (await api.post<CompletionReply>(`/api/v1/sessions/${sessionId}/complete`, { completion_id: completionId, position })).data;

export const sendEvents = async (sessionId: string, events: PlaybackEvent[]): Promise<void> => {
  await api.post(`/api/v1/sessions/${sessionId}/events`, { events });
};

const sendBeforeLeaving = (method: string, path: string, body?: unknown): void => {
  const token = localStorage.getItem('accessToken');
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };

  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  fetch(`${api.defaults.baseURL ?? ''}${path}`, {
    method,
    keepalive: true,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  }).catch(() => undefined);
};

export const endSessionBeforeLeaving = (sessionId: string): void =>
  sendBeforeLeaving('DELETE', `/api/v1/sessions/${sessionId}`);

export const sendEventsBeforeLeaving = (sessionId: string, events: PlaybackEvent[]): void =>
  sendBeforeLeaving('POST', `/api/v1/sessions/${sessionId}/events`, { events });
