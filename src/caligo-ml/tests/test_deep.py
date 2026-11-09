import json

import joblib
import numpy as np
import pytest
from tiny_onnx import GENRE_CLASSES, spectrogram_model, text_model, word_tokenizer

from caligo_ml.datasets import spam_data
from caligo_ml.deep.onnx_models import OnnxSpectrogramClassifier, OnnxTextClassifier
from caligo_ml.evaluation import classification_metrics, reference_bins
from caligo_ml.features.audio import fit_frames, spectrogram_profile
from caligo_ml.registry import Registry
from caligo_ml.tasks import TASKS
from caligo_ml.training import train


@pytest.fixture(scope="module")
def mels():
    rng = np.random.default_rng(4)
    return [rng.normal(-30, 10, (128, frames)).astype(np.float32) for frames in (200, 431, 1292, 900)]


def test_the_spectrogram_model_averages_over_crops_of_any_length(mels):
    model = OnnxSpectrogramClassifier(spectrogram_model(), GENRE_CLASSES, mean=-30.0, std=10.0)
    chances, embeddings = model.run(mels)

    assert chances.shape == (4, len(GENRE_CLASSES))
    np.testing.assert_allclose(chances.sum(axis=1), 1, rtol=1e-5)
    assert embeddings.shape == (4, 6)
    assert set(model.predict(mels)) <= set(GENRE_CLASSES)
    assert model.crops(mels[0]).shape == (1, 1, 128, 431)
    assert model.crops(mels[2]).shape == (3, 1, 128, 431)


def test_onnx_models_survive_pickling_without_their_session(tmp_path, mels):
    model = OnnxSpectrogramClassifier(spectrogram_model(), GENRE_CLASSES)
    before = model.predict_proba(mels[:2])
    joblib.dump(model, tmp_path / "model.joblib")

    np.testing.assert_allclose(joblib.load(tmp_path / "model.joblib").predict_proba(mels[:2]), before)


def test_the_text_model_tokenizes_in_batches():
    model = OnnxTextClassifier(text_model(), word_tokenizer(), max_length=8, batch_size=2)
    chances = model.predict_proba(["this is bad", "a good song", "bad bad bad", "good"])

    assert chances.shape == (4, 2)
    assert (chances[:, 1] > 0.5).tolist() == [True, False, True, False]
    assert model.predict(["bad"]).tolist() == [1]


def deep_bundle(folder, mels, labels, ids):
    (folder / "resnet18").mkdir(parents=True)
    (folder / "resnet18" / "model.onnx").write_bytes(spectrogram_model())
    (folder / "resnet18" / "model.json").write_text(json.dumps(
        {"kind": "spectrogram", "classes": list(GENRE_CLASSES), "mean": -30.0, "std": 10.0, "crop": 431}))
    model = OnnxSpectrogramClassifier(spectrogram_model(), GENRE_CLASSES, -30.0, 10.0)
    metrics = classification_metrics(labels, model.predict(mels), list(GENRE_CLASSES))
    joblib.dump({"X": mels, "y": labels, "ids": ids}, folder / "test.joblib")
    stacked = np.stack([mel[:, :431] for mel in mels])
    (folder / "summary.json").write_text(json.dumps({
        "task": "genre_cnn", "source": "synthetic", "sha256": "cnn", "rows": {"train": 0, "validation": 0, "test": 4},
        "environment": {"python": "3.13", "torch": "2.9"},
        "drift_reference": {name: reference_bins(values) for name, values in spectrogram_profile(stacked).items()},
        "best": "resnet18",
        "results": [{"candidate": "resnet18", "train_rows": 0, "fit_seconds": 1.0, "params": {"backbone": "tiny"},
                     "validation": {**metrics, "macro_f1": 0.6}, "test": {**metrics, "macro_f1": 0.6},
                     "predictions": {"counts": {"Rock": 4}}}],
    }))
    return folder


def test_the_registry_takes_an_onnx_bundle(tmp_path, mels):
    labels = np.array(["Rock", "Pop", "Folk", "Rock"])
    folder = deep_bundle(tmp_path / "bundle", [fit_frames(mel, 431) for mel in mels], labels, np.arange(4))
    registry = Registry(tmp_path / "ml")
    version, decision = registry.register(folder)
    exported = joblib.load(tmp_path / "ml" / "models" / "genre_cnn" / f"v{version}" / "model.joblib")

    assert decision.promote
    assert isinstance(exported, OnnxSpectrogramClassifier)
    assert exported.predict_proba(mels[:1]).shape == (1, len(GENRE_CLASSES))


def text_bundle(folder, texts, labels, sha):
    (folder / "distilbert").mkdir(parents=True)
    (folder / "distilbert" / "model.onnx").write_bytes(text_model())
    (folder / "distilbert" / "tokenizer.json").write_text(word_tokenizer())
    (folder / "distilbert" / "model.json").write_text(json.dumps({"kind": "text", "max_length": 32, "classes": [0, 1]}))
    joblib.dump({"X": texts, "y": labels, "ids": np.arange(len(texts))}, folder / "test.joblib")
    perfect = {"pr_auc": 1.0, "roc_auc": 1.0, "precision": 1.0, "recall": 1.0, "f1": 1.0}
    profile = TASKS["spam"].drift_profile(texts)
    (folder / "summary.json").write_text(json.dumps({
        "task": "spam", "source": "YouTube Spam Collection", "sha256": sha,
        "rows": {"train": 0, "validation": 0, "test": len(texts)}, "environment": {"python": "3.13"},
        "drift_reference": {name: reference_bins(values) for name, values in profile.items()},
        "best": "distilbert",
        "results": [{"candidate": "distilbert", "train_rows": 0, "fit_seconds": 1.0, "params": {"base": "tiny"},
                     "validation": perfect, "test": perfect, "predictions": {"score": reference_bins([0.1, 0.9])}}],
    }))
    return folder


def test_a_text_champion_without_rules_keeps_the_earlier_rules(tmp_path):
    data = spam_data()
    candidates = {name: model for name, model in TASKS["spam"].candidates().items()
                  if name in ("ripper", "naive_bayes")}
    summary = train(TASKS["spam"], data, tmp_path / "words", candidates)
    registry = Registry(tmp_path / "ml")
    first, _ = registry.register(tmp_path / "words")
    texts = np.array(["bad words here", "a good song"], dtype=object)
    second, decision = registry.register(text_bundle(tmp_path / "deep", texts, np.array([1, 0]), summary["sha256"]))
    exported = tmp_path / "ml" / "models" / "spam" / f"v{second}"

    assert decision.promote
    assert isinstance(joblib.load(exported / "model.joblib"), OnnxTextClassifier)
    assert (exported / "rules.joblib").exists()
    assert joblib.load(exported / "rules.joblib").rules_ == joblib.load(
        tmp_path / "ml" / "models" / "spam" / f"v{first}" / "rules.joblib").rules_
