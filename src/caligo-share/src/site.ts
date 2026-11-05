const TIMEOUT_MS = 5000;
const MAX_IMAGE_BYTES = 8 * 1024 * 1024;

const IMAGE_TYPES: Record<string, string> = {
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  webp: 'image/webp',
  gif: 'image/gif'
};

const imageType = (imagePath: string, header: string | null): string | null => {
  if (header?.startsWith('image/')) {
    return header.split(';')[0].trim();
  }

  const extension = imagePath.split('?')[0].split('.').pop()?.toLowerCase() ?? '';

  return IMAGE_TYPES[extension] ?? null;
};

export const createSite = (origin: string) => ({
  api: async (apiPath: string): Promise<unknown> => {
    const response = await fetch(`${origin}${apiPath}`, {
      headers: { Accept: 'application/json' },
      signal: AbortSignal.timeout(TIMEOUT_MS)
    });

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      throw new Error(`${apiPath} answered ${response.status}.`);
    }

    return response.json();
  },

  image: async (imagePath: string): Promise<string | null> => {
    try {
      const response = await fetch(`${origin}${imagePath}`, { signal: AbortSignal.timeout(TIMEOUT_MS) });
      const type = imageType(imagePath, response.headers.get('content-type'));

      if (!response.ok || !type) {
        return null;
      }

      const bytes = Buffer.from(await response.arrayBuffer());

      return bytes.length > MAX_IMAGE_BYTES ? null : `data:${type};base64,${bytes.toString('base64')}`;
    } catch {
      return null;
    }
  }
});
