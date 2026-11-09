import type { SongAnalysis } from './types';

export const GENRES = ['Electronic', 'Experimental', 'Folk', 'Hip-Hop', 'Instrumental', 'International', 'Pop', 'Rock'];

export const mixtapeFolder = (uri: string | null | undefined) => {
  const parts = uri?.split('/') ?? [];

  if (parts.length < 4 || parts[0] !== '' || parts[1] !== 'mixtape') {
    return null;
  }

  try {
    return decodeURIComponent(parts[2]);
  } catch {
    return null;
  }
};

const high = (value: number | null) => value !== null && value >= 0.66;
const low = (value: number | null) => value !== null && value <= 0.33;

export const soundWords = (song: SongAnalysis) => {
  const words: string[] = [];
  const genre = song.genreLabel ?? song.genre;

  if (genre) {
    words.push(genre);
  }

  if (high(song.energy)) {
    words.push('energetic');
  } else if (low(song.energy)) {
    words.push('calm');
  }

  if (high(song.valence)) {
    words.push('bright');
  } else if (low(song.valence)) {
    words.push('dark');
  }

  if (high(song.danceability)) {
    words.push('danceable');
  }

  if (genre !== 'Instrumental') {
    if (high(song.instrumental)) {
      words.push('instrumental');
    } else if (low(song.instrumental)) {
      words.push('vocal');
    }
  }

  return words;
};
