import axios from 'axios';
import type { ContentKind, Plan } from './types';

export const KINDS: { value: ContentKind | ''; label: string }[] = [
  { value: '', label: 'Everything' },
  { value: 'movie', label: 'Movies' },
  { value: 'episode', label: 'Episodes' },
  { value: 'song', label: 'Songs' }
];

export const kindLabel = (kind: ContentKind): string => {
  switch (kind) {
    case 'movie':
      return 'Movie';
    case 'episode':
      return 'Episode';
    case 'song':
      return 'Song';
    default:
      return 'Title';
  }
};

export const watchVerb = (kind: ContentKind): string => (kind === 'song' ? 'listen' : 'watch');

export const formatDay = (value: string | null | undefined): string => {
  if (!value) {
    return '';
  }

  const [year, month, day] = value.slice(0, 10).split('-').map(Number);

  return new Date(year, month - 1, day).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric'
  });
};

export const yearOf = (value: string | null | undefined): string => (value ? value.slice(0, 4) : '');

export const formatDuration = (minutes: number, kind: ContentKind): string => {
  if (kind === 'song') {
    const seconds = Math.round(minutes * 60);

    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  const whole = Math.round(minutes);

  return whole >= 60 ? `${Math.floor(whole / 60)}h ${String(whole % 60).padStart(2, '0')}m` : `${whole}m`;
};

export const formatRating = (rating: number | null | undefined): string =>
  rating == null || rating <= 0 ? 'not rated yet' : `${rating.toFixed(1)} / 10`;

export const formatAmount = (amount: number): string => `€${amount.toFixed(2)}`;

export const formatCount = (value: number | null | undefined): string =>
  value == null ? '' : Math.round(value).toLocaleString();

export const planName = (plan: Plan): string => {
  try {
    const info = JSON.parse(plan.planInfo) as { name?: string };

    return info.name ?? `Plan ${plan.subscriptionPlanId}`;
  } catch {
    return `Plan ${plan.subscriptionPlanId}`;
  }
};

export const planFacts = (plan: Plan): string[] => {
  try {
    const info = JSON.parse(plan.planInfo) as Record<string, unknown>;

    return Object.entries(info)
      .filter(([key]) => key !== 'name')
      .map(([key, value]) => `${key.replace(/_/g, ' ')}: ${Array.isArray(value) ? value.join(', ') : String(value)}`);
  } catch {
    return [];
  }
};

export const statusOf = (err: unknown): number | undefined =>
  axios.isAxiosError(err) ? err.response?.status : undefined;

export const catalogError = (err: unknown, fallback: string): string => {
  if (axios.isAxiosError(err) && typeof err.response?.data === 'string' && err.response.data.trim().length > 0) {
    return err.response.data;
  }

  switch (statusOf(err)) {
    case 401:
      return 'Please sign in first.';
    case 403:
      return "You don't have permission to do that.";
    case 404:
      return 'It is not in the Thesaurarium.';
    case 503:
      return 'The Thesaurarium isn\'t available right now.';
    default:
      return fallback;
  }
};

export const PROFILE_REQUIRED = 'Create your streaming profile first.';
