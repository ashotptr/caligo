import type { AxiosResponse } from 'axios';

// The version to send back as If-Match. The API's ETag is the post's version in quotes, so the
// body's version stands in when the header isn't readable (an API that doesn't expose it to CORS).
export const etagOf = (response: AxiosResponse<{ version: number }>): string => {
  const header = response.headers.etag;

  return typeof header === 'string' ? header : `"${response.data.version}"`;
};
