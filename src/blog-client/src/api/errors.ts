import axios from 'axios';

// The API answers 400, 409 and 412 with the reason as a plain string; show it when there is one.
export const apiError = (err: unknown, fallback: string): string => {
  if (!axios.isAxiosError(err) || !err.response) {
    return fallback;
  }

  const { status, data } = err.response;

  if (typeof data === 'string' && data.trim().length > 0) {
    return data;
  }

  switch (status) {
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

// 412: the If-Match version is stale. 409: another write won the race while this one ran.
export const isEditConflict = (err: unknown): boolean =>
  axios.isAxiosError(err) && (err.response?.status === 409 || err.response?.status === 412);
