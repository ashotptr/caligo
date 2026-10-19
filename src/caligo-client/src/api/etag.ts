import type { AxiosResponse } from 'axios';

export const etagOf = (response: AxiosResponse<{ version: number }>): string => {
  const header = response.headers.etag;

  return typeof header === 'string' ? header : `"${response.data.version}"`;
};
