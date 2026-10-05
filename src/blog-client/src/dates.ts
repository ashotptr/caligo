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

export const toLocalInput = (date: Date): string =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;

export const fromLocalInput = (value: string): string => new Date(value).toISOString();
