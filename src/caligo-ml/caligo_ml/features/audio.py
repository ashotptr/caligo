import io
import warnings
from pathlib import Path

import librosa
import numpy as np
import pandas as pd
import soundfile
from scipy import stats

RATE = 44100
CLIP_SECONDS = 30.0
MEL_RATE = 22050
MEL_BANDS = 128
MEL_HOP = 512
MEL_FRAMES = 1292
BAND_GROUPS = {"low": slice(0, 32), "mid": slice(32, 96), "high": slice(96, 128)}
SIZES = {
    "chroma_stft": 12, "chroma_cqt": 12, "chroma_cens": 12, "tonnetz": 6, "mfcc": 20, "rmse": 1, "zcr": 1,
    "spectral_centroid": 1, "spectral_bandwidth": 1, "spectral_contrast": 7, "spectral_rolloff": 1,
}
MOMENTS = ("mean", "std", "skew", "kurtosis", "median", "min", "max")
DRIFT_FEATURES = [
    ("spectral_centroid", "mean", "01"), ("spectral_bandwidth", "mean", "01"), ("spectral_rolloff", "mean", "01"),
    ("rmse", "mean", "01"), ("zcr", "mean", "01"), ("mfcc", "mean", "01"), ("mfcc", "mean", "02"),
    ("spectral_contrast", "mean", "04"), ("chroma_cens", "std", "01"), ("tonnetz", "std", "01"),
]


def columns():
    keys = [(name, moment, f"{number:02d}") for name, size in SIZES.items() for moment in MOMENTS
            for number in range(1, size + 1)]
    return pd.MultiIndex.from_tuples(keys, names=("feature", "statistics", "number")).sort_values()


def drift_profile(X):
    index = columns()
    return {"/".join(key): np.asarray(X, dtype=float)[:, index.get_loc(key)] for key in DRIFT_FEATURES}


def log_mel(samples, rate=RATE):
    if rate != MEL_RATE:
        samples = librosa.resample(samples, orig_sr=rate, target_sr=MEL_RATE)
    mel = librosa.feature.melspectrogram(y=samples, sr=MEL_RATE, n_fft=2048, hop_length=MEL_HOP, n_mels=MEL_BANDS)
    return librosa.power_to_db(mel, ref=1.0, top_db=None).astype(np.float32)


def fit_frames(mel, frames=MEL_FRAMES):
    if mel.shape[1] >= frames:
        return mel[:, :frames]
    return np.pad(mel, ((0, 0), (0, frames - mel.shape[1])), constant_values=float(mel.min()))


def spectrogram_profile(mels):
    mels = np.asarray(mels, dtype=float)
    return {name: mels[:, bands].mean(axis=(1, 2)) for name, bands in BAND_GROUPS.items()}


def load_clip(path, seconds=CLIP_SECONDS):
    data = io.BytesIO(Path(path).read_bytes())
    duration = soundfile.info(data).duration
    data.seek(0)
    samples, _ = librosa.load(data, sr=RATE, mono=True, offset=max(0.0, (duration - seconds) / 2), duration=seconds)
    return samples


def problem(samples, rate=RATE):
    if len(samples) < 5 * rate:
        return "too short"
    if np.sqrt(np.mean(np.square(samples, dtype=np.float64))) < 1e-4:
        return "silent"
    return None


def moments(values):
    with warnings.catch_warnings(), np.errstate(all="ignore"):
        warnings.simplefilter("ignore", RuntimeWarning)
        return {
            "mean": np.mean(values, axis=1),
            "std": np.std(values, axis=1),
            "skew": np.nan_to_num(stats.skew(values, axis=1), nan=0.0),
            "kurtosis": np.nan_to_num(stats.kurtosis(values, axis=1), nan=0.0),
            "median": np.median(values, axis=1),
            "min": np.min(values, axis=1),
            "max": np.max(values, axis=1),
        }


def features(samples, rate=RATE):
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message="Trying to estimate tuning from empty frequency set")
        return compute(samples, rate)


def compute(samples, rate):
    values = {}

    def add(name, matrix):
        for moment, numbers in moments(matrix).items():
            for number, value in enumerate(numbers, start=1):
                values[name, moment, f"{number:02d}"] = value

    add("zcr", librosa.feature.zero_crossing_rate(samples, frame_length=2048, hop_length=512))

    cqt = np.abs(librosa.cqt(samples, sr=rate, hop_length=512, bins_per_octave=12, n_bins=7 * 12, tuning=None,
                             pad_mode="reflect"))
    add("chroma_cqt", librosa.feature.chroma_cqt(C=cqt, n_chroma=12, n_octaves=7, bins_per_octave=12))
    cens = librosa.feature.chroma_cens(C=cqt, n_chroma=12, n_octaves=7, bins_per_octave=12)
    add("chroma_cens", cens)
    add("tonnetz", librosa.feature.tonnetz(chroma=cens))
    del cqt

    stft = np.abs(librosa.stft(samples, n_fft=2048, hop_length=512, pad_mode="reflect"))
    add("chroma_stft", librosa.feature.chroma_stft(S=stft ** 2, n_chroma=12))
    add("rmse", librosa.feature.rms(S=stft))
    add("spectral_centroid", librosa.feature.spectral_centroid(S=stft))
    add("spectral_bandwidth", librosa.feature.spectral_bandwidth(S=stft))
    add("spectral_contrast", librosa.feature.spectral_contrast(S=stft, n_bands=6))
    add("spectral_rolloff", librosa.feature.spectral_rolloff(S=stft))

    mel = librosa.feature.melspectrogram(sr=rate, S=stft ** 2)
    add("mfcc", librosa.feature.mfcc(S=librosa.power_to_db(mel), n_mfcc=20))

    index = columns()
    return pd.Series([values[key] for key in index], index=index, dtype=np.float32)
