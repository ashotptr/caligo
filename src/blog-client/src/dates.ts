// The API keeps every time in UTC. PostgreSQL answers with a trailing Z, SQLite can leave it off,
// and a time without a zone would otherwise be read as local time.
export const parseServerDate = (value: string): Date =>
  new Date(/(?:[zZ]|[+-]\d\d:?\d\d)$/.test(value) ? value : `${value}Z`);

export const formatDate = (value: string): string =>
  parseServerDate(value).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });

export const formatDateTime = (value: string): string =>
  parseServerDate(value).toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  });

const pad = (n: number) => String(n).padStart(2, '0');

// <input type="datetime-local"> works in the browser's time zone, without one in the value.
export const toLocalInput = (date: Date): string =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;

// The input's value has no zone, so it parses as local time; the API gets it as UTC.
export const fromLocalInput = (value: string): string => new Date(value).toISOString();
