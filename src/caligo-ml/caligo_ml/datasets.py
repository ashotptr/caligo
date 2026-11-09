import hashlib
import shutil
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

from caligo_ml.features.audio import columns
from caligo_ml.features.text import clean_comment

FMA_METADATA = "https://os.unil.cloud.switch.ch/fma/fma_metadata.zip"
AGENT = "caligo-ml/1.0 (+https://github.com/ashotptr/caligo)"
YOUTUBE_SPAM = Path(__file__).resolve().parents[1] / "tests" / "data" / "youtube-spam"
GENRES = ["Electronic", "Experimental", "Folk", "Hip-Hop", "Instrumental", "International", "Pop", "Rock"]
MOODS = ["energy", "valence", "danceability"]
TOXIC_LABELS = ["toxic", "severe_toxic", "obscene", "threat", "insult", "identity_hate"]


@dataclass
class Dataset:
    X: np.ndarray
    y: np.ndarray
    ids: np.ndarray
    source: str
    sha256: str


def digest(*paths):
    hasher = hashlib.sha256()
    for path in paths:
        with Path(path).open("rb") as file:
            for block in iter(lambda: file.read(1 << 20), b""):
                hasher.update(block)
    return hasher.hexdigest()


def download(url, folder):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / url.rsplit("/", 1)[1]
    if not target.exists():
        partial = target.with_name(target.name + ".part")
        request = urllib.request.Request(url, headers={"User-Agent": AGENT})
        with urllib.request.urlopen(request) as response, partial.open("wb") as file:
            shutil.copyfileobj(response, file, length=1 << 20)
        partial.replace(target)
    return target


def fma_tables(cache):
    archive = download(FMA_METADATA, cache)
    with zipfile.ZipFile(archive) as bundle:
        tracks = pd.read_csv(bundle.open("fma_metadata/tracks.csv"), index_col=0, header=[0, 1], low_memory=False)
        features = pd.read_csv(bundle.open("fma_metadata/features.csv"), index_col=0, header=[0, 1, 2])
        echonest = pd.read_csv(bundle.open("fma_metadata/echonest.csv"), index_col=0, header=[0, 1, 2])
    return tracks, features[columns()].astype(np.float32), echonest, digest(archive)


def genre_data(cache):
    tracks, features, _, sha = fma_tables(cache)
    genre = tracks["track", "genre_top"]
    ids = genre.index[genre.isin(GENRES)].intersection(features.index)
    return Dataset(features.loc[ids].to_numpy(), genre.loc[ids].to_numpy(dtype=str), ids.to_numpy(),
                   "FMA metadata: features.csv and tracks.csv", sha)


def echonest_data(cache, target):
    _, features, echonest, sha = fma_tables(cache)
    measured = echonest["echonest", "audio_features"]
    ids = measured.index.intersection(features.index)
    return Dataset(features.loc[ids].to_numpy(), target(measured.loc[ids]), ids.to_numpy(),
                   "FMA metadata: features.csv and echonest.csv", sha)


def mood_data(cache):
    return echonest_data(cache, lambda measured: measured[MOODS].to_numpy(np.float32))


def instrumental_data(cache):
    return echonest_data(cache, lambda measured: (measured["instrumentalness"] >= 0.5).to_numpy(dtype=int))


def spam_data(folder=YOUTUBE_SPAM):
    paths = sorted(Path(folder).glob("*.csv"))
    comments = pd.concat([pd.read_csv(path) for path in paths], ignore_index=True)
    ids = comments.content.map(clean_comment).to_numpy()
    return Dataset(comments.content.to_numpy(dtype=object), comments.spam.to_numpy(dtype=int), ids,
                   "YouTube Spam Collection", digest(*paths))


def jigsaw_file(source):
    source = Path(source)
    if source.is_file():
        return source
    return next(path for path in sorted(source.rglob("train.csv*")) if "jigsaw" in path.as_posix().lower())


def toxicity_data(source):
    path = jigsaw_file(source)
    comments = pd.read_csv(path)
    flagged = (comments[TOXIC_LABELS].max(axis=1) > 0).to_numpy(dtype=int)
    return Dataset(comments.comment_text.to_numpy(dtype=object), flagged, comments.id.to_numpy(dtype=str),
                   "Jigsaw Toxic Comment Classification", digest(path))
