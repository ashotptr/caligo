export const THESAURARIUM = '/thesaurarium';
export const TITLES = `${THESAURARIUM}/titles`;
export const SERIES = `${THESAURARIUM}/series`;
export const ARTISTS = `${THESAURARIUM}/artists`;
export const COLLECTIONS = `${THESAURARIUM}/collections`;

export const titlePath = (contentId: number): string => `${TITLES}/${contentId}`;
export const albumPath = (albumId: number): string => `${THESAURARIUM}/albums/${albumId}`;
export const singlePath = (singleId: number): string => `${THESAURARIUM}/singles/${singleId}`;
export const releasePath = (kind: 'album' | 'single', id: number): string =>
  kind === 'album' ? albumPath(id) : singlePath(id);
export const artistPath = (artistId: number): string => `${ARTISTS}/${artistId}`;
export const seriesPath = (seriesId: number): string => `${SERIES}/${seriesId}`;
export const actorPath = (actorId: number): string => `${THESAURARIUM}/actors/${actorId}`;
export const collectionPath = (collectionId: number): string => `${COLLECTIONS}/${collectionId}`;

export const inThesaurarium = (pathname: string): boolean =>
  pathname === THESAURARIUM || pathname.startsWith(`${THESAURARIUM}/`);

const MIXTAPE = '/mixtape/';

export const mixtapePath = (audioUri: string | null | undefined): string | null => {
  if (!audioUri) {
    return null;
  }

  const at = audioUri.indexOf(MIXTAPE);

  if (at < 0) {
    return null;
  }

  try {
    return decodeURIComponent(audioUri.slice(at + MIXTAPE.length));
  } catch {
    return null;
  }
};
