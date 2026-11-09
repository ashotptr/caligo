# FMA sample

Audio features of 1,400 tracks from the Free Music Archive dataset, for the quality gate.

- 800 tracks, 100 for each of the 8 genres, with the 518 librosa features and the top genre.
- 600 tracks with Echo Nest values: energy, valence, danceability and instrumentalness.
- Features are stored as float16 to keep the file small.

Source: `fma_metadata.zip` (SHA-1 f0df49ffe5f2a6008d7dc83c6915b31835dfe733) from https://github.com/mdeff/fma.
Michaël Defferrard, Kirell Benzi, Pierre Vandergheynst, Xavier Bresson. "FMA: A Dataset for Music Analysis", ISMIR 2017.
License: CC BY 4.0 for the metadata and features.

Rebuild from this folder's parent project:

```
PYTHONPATH=. python tests/data/fma-sample/build.py path/to/fma_metadata.zip
```
