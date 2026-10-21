import axios from 'axios';
import api from './api/axios';

export type StreamTone = 'muted' | 'main' | 'success' | 'error' | 'done';

export const toneOf = (line: string, previous: StreamTone): StreamTone => {
  if (line.includes('Downloading')) return 'main';
  if (line.includes('Queueing')) return 'muted';
  if (line.includes('SUCCESS')) return 'success';
  if (line.includes('ERROR') || line.includes('Failed')) return 'error';

  return previous;
};

export const streamFailure = (err: unknown): string => {
  if (axios.isAxiosError(err) && err.response) {
    const data: unknown = err.response.data;

    if (typeof data === 'string' && data.trim().length > 0 && !data.trim().startsWith('<')) {
      return data.trim();
    }

    if (err.response.status === 401) {
      return 'ACCESS DENIED: LOGIN REQUIRED';
    }

    if (err.response.status === 403) {
      return "You don't have permission to do that.";
    }
  }

  return 'CONNECTION ERROR';
};

export const streamLines = async (url: string, body: unknown, onLine: (line: string) => void): Promise<boolean> => {
  let seen = 0;
  let pending = '';
  let ended = false;

  const read = (text: string) => {
    pending += text.slice(seen);
    seen = text.length;

    let boundary = pending.indexOf('\n\n');

    while (boundary >= 0) {
      const block = pending.slice(0, boundary);

      pending = pending.slice(boundary + 2);
      boundary = pending.indexOf('\n\n');

      if (block.startsWith('event: end')) {
        ended = true;
        continue;
      }

      const line = block
        .split('\n')
        .filter(part => part.startsWith('data: '))
        .map(part => part.slice(6))
        .join(' ')
        .trim();

      if (line) {
        onLine(line);
      }
    }
  };

  const response = await api.post<string>(url, body, {
    responseType: 'text',
    onDownloadProgress: event => {
      const target = event.event?.target as XMLHttpRequest | undefined;

      if (target && typeof target.responseText === 'string') {
        read(target.responseText);
      }
    }
  });

  read(typeof response.data === 'string' ? response.data : '');

  return ended;
};
