import sys
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

from caligo_ml.datasets import GENRES, MOODS
from caligo_ml.features.audio import columns

PER_GENRE = 100
MOOD_ROWS = 600
SEED = 42


def single_columns(bundle, name, keys):
    head = pd.read_csv(bundle.open(name), index_col=0, header=list(range(len(keys[0]))), nrows=1)
    wanted = {head.columns.get_loc(key) + 1: key[-1] for key in keys}
    frame = pd.read_csv(bundle.open(name), header=None, skiprows=len(keys[0]) + 1, usecols=[0, *wanted],
                        index_col=0, low_memory=False)
    frame.columns = [wanted[position] for position in sorted(wanted)]
    return frame


def main(archive, out):
    with zipfile.ZipFile(archive) as bundle:
        genre = single_columns(bundle, "fma_metadata/tracks.csv", [("track", "genre_top")]).genre_top
        measured = single_columns(bundle, "fma_metadata/echonest.csv",
                                  [("echonest", "audio_features", name) for name in [*MOODS, "instrumentalness"]])
        chosen = genre[genre.isin(GENRES)]
        genre_ids = chosen.groupby(chosen).sample(PER_GENRE, random_state=SEED).index.sort_values()
        mood_ids = measured.sample(MOOD_ROWS, random_state=SEED).index.sort_values()
        wanted = genre_ids.union(mood_ids)
        reader = pd.read_csv(bundle.open("fma_metadata/features.csv"), index_col=0, header=[0, 1, 2], chunksize=5000)
        features = pd.concat(chunk.loc[chunk.index.intersection(wanted), columns()] for chunk in reader)

    largest = float(features.abs().max().max())
    if largest >= np.finfo(np.float16).max:
        raise ValueError(f"A feature reaches {largest}, too large for float16.")
    np.savez_compressed(
        out,
        genre_X=features.loc[genre_ids].to_numpy(np.float16),
        genre_y=genre.loc[genre_ids].to_numpy(dtype=str),
        genre_ids=genre_ids.to_numpy(),
        mood_X=features.loc[mood_ids].to_numpy(np.float16),
        mood_y=measured.loc[mood_ids, MOODS].to_numpy(np.float32),
        instrumentalness=measured.loc[mood_ids, "instrumentalness"].to_numpy(np.float32),
        mood_ids=mood_ids.to_numpy(),
    )
    print(f"{len(genre_ids)} genre rows, {len(mood_ids)} mood rows, largest value {largest:.1f}, saved to {out}")


if __name__ == "__main__":
    main(sys.argv[1], Path(__file__).with_name("fma-sample.npz"))
