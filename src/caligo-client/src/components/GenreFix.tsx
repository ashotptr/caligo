import { useLabelGenreMutation } from '../api/mlApi';
import { apiError } from '../api/errors';
import { GENRES } from '../ml';
import { useToast } from '../store/hooks';
import type { SongAnalysis } from '../types';

const GenreFix = ({ song, folder }: { song: SongAnalysis; folder: string | null }) => {
  const [labelGenre, { isLoading }] = useLabelGenreMutation();
  const toast = useToast();
  const current = song.genreLabel ?? song.genre ?? '';

  const change = async (genre: string) => {
    try {
      await labelGenre({ uri: song.uri, genre, folder }).unwrap();
      toast(`Genre set to ${genre}.`);
    } catch (error) {
      toast(apiError(error, 'Could not set the genre.'));
    }
  };

  return (
    <label className="genre-fix">
      Genre
      <select value={current} disabled={isLoading} onChange={event => void change(event.target.value)}>
        {!GENRES.includes(current) && <option value={current}>{current || '—'}</option>}
        {GENRES.map(genre => <option key={genre} value={genre}>{genre}</option>)}
      </select>
    </label>
  );
};

export default GenreFix;
