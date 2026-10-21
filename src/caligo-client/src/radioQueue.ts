import { useSyncExternalStore } from 'react';
import api from './api/axios';
import { parseNowPlaying } from './radio';

export type QueueState = 'QUEUE' | 'SYNCING' | 'WAITING' | 'LIVE' | 'ERROR';

interface Entry {
  id: string;
  state: QueueState;
  trackId?: string;
}

interface QueueAnswer {
  success: boolean;
  message: string;
  trackId: string | null;
}

const STORAGE_KEY = 'caligo_radio_queue';
const EVENTS = '/radio/events';
const ERROR_MS = 3000;
const STATES: QueueState[] = ['QUEUE', 'SYNCING', 'WAITING', 'LIVE', 'ERROR'];

const restored = (raw: unknown): Entry | null => {
  if (!raw || typeof raw !== 'object') {
    return null;
  }

  const { id, state, trackId } = raw as Record<string, unknown>;

  if (typeof id !== 'string' || !STATES.includes(state as QueueState)) {
    return null;
  }

  const track = typeof trackId === 'string' && trackId.length > 0 ? trackId : undefined;
  const following = (state === 'WAITING' || state === 'LIVE') && track;

  return following ? { id, state: state as QueueState, trackId: track } : { id, state: 'QUEUE' };
};

const load = (): Entry[] => {
  try {
    const raw: unknown = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? '[]');

    return Array.isArray(raw) ? raw.map(restored).filter((entry): entry is Entry => entry !== null) : [];
  } catch {
    return [];
  }
};

let entries: Entry[] = load();
let events: EventSource | null = null;
const listeners = new Set<() => void>();

const save = (): void => {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(entries));
  } catch {
    return;
  }
};

const following = (): boolean => entries.some(entry => (entry.state === 'WAITING' || entry.state === 'LIVE') && entry.trackId);

const heard = (raw: string): void => {
  const playing = parseNowPlaying(raw)?.file ?? null;
  let changed = false;

  entries = entries.map(entry => {
    if (!entry.trackId) {
      return entry;
    }

    if (entry.state === 'WAITING' && playing === entry.trackId) {
      changed = true;

      return { ...entry, state: 'LIVE' };
    }

    if (entry.state === 'LIVE' && playing !== entry.trackId) {
      changed = true;

      return { id: entry.id, state: 'QUEUE' };
    }

    return entry;
  });

  if (changed) {
    commit();
  }
};

const watch = (): void => {
  if (following() && !events) {
    events = new EventSource(EVENTS);
    events.onmessage = event => heard(String(event.data));
  } else if (!following() && events) {
    events.close();
    events = null;
  }
};

const commit = (): void => {
  save();
  listeners.forEach(listener => listener());
  watch();
};

const set = (id: string, state: QueueState, trackId?: string): void => {
  const entry: Entry = trackId ? { id, state, trackId } : { id, state };

  entries = entries.some(existing => existing.id === id)
    ? entries.map(existing => (existing.id === id ? entry : existing))
    : [...entries, entry];

  commit();
};

const subscribe = (listener: () => void): (() => void) => {
  listeners.add(listener);
  watch();

  return () => {
    listeners.delete(listener);
  };
};

const stateOf = (id: string): QueueState => entries.find(entry => entry.id === id)?.state ?? 'QUEUE';

export const useQueueState = (id: string): QueueState =>
  useSyncExternalStore(subscribe, () => stateOf(id), () => 'QUEUE');

export const queueToRadio = async (id: string, contentId: number): Promise<boolean> => {
  set(id, 'SYNCING');

  try {
    const { data } = await api.post<QueueAnswer>('/api/caligo/radio-queue', { contentId });

    if (!data.success) {
      throw new Error(data.message);
    }

    set(id, 'WAITING', data.trackId ?? undefined);

    return true;
  } catch {
    set(id, 'ERROR');
    window.setTimeout(() => set(id, 'QUEUE'), ERROR_MS);

    return false;
  }
};
