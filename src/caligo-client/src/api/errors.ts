import axios from 'axios';

export interface ApiFailure {
  status: number | undefined;
  data: unknown;
}

export const failureOf = (err: unknown): ApiFailure | undefined => {
  if (axios.isAxiosError(err)) {
    return err.response ? { status: err.response.status, data: err.response.data } : undefined;
  }

  if (err !== null && typeof err === 'object' && 'status' in err) {
    const { status, data } = err as { status: unknown; data?: unknown };

    return { status: typeof status === 'number' ? status : undefined, data };
  }

  return undefined;
};

export const statusOf = (err: unknown): number | undefined => failureOf(err)?.status;

export const textOf = (err: unknown): string | undefined => {
  const data = failureOf(err)?.data;

  return typeof data === 'string' && data.trim().length > 0 ? data : undefined;
};

export const apiError = (err: unknown, fallback: string): string => {
  const failure = failureOf(err);

  if (!failure || failure.status === undefined) {
    return fallback;
  }

  const text = textOf(err);

  if (text) {
    return text;
  }

  switch (failure.status) {
    case 401:
      return 'Please sign in again.';
    case 403:
      return "You don't have permission to do that.";
    case 404:
      return 'It no longer exists.';
    case 409:
    case 412:
      return 'Someone else changed this post since you opened it.';
    default:
      return fallback;
  }
};

export const isEditConflict = (err: unknown): boolean => {
  const status = statusOf(err);

  return status === 409 || status === 412;
};
