import json
from unittest import mock

import numpy as np
import pytest
from sklearn.datasets import make_classification
from sklearn.dummy import DummyClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

from caligo_ml import registry as registry_module
from caligo_ml.datasets import Dataset
from caligo_ml.features.audio import DRIFT_FEATURES, columns
from caligo_ml.registry import Registry
from caligo_ml.tasks import TASKS
from caligo_ml.training import train

TASK = TASKS["instrumental"]


@pytest.fixture(scope="module")
def songs():
    X, y = make_classification(n_samples=600, n_features=len(columns()), n_informative=12, random_state=3)
    return Dataset(X.astype(np.float32), y, np.arange(600), "synthetic", "abc123")


def bundle(tmp_path, songs, name, candidates):
    return tmp_path / name, train(TASK, songs, tmp_path / name, candidates)


def strong():
    return {"logistic_regression": make_pipeline(StandardScaler(), LogisticRegression(max_iter=2000)),
            "always_vocal": DummyClassifier(strategy="most_frequent")}


def weak():
    return {"always_vocal": DummyClassifier(strategy="most_frequent")}


def test_training_picks_on_validation_and_writes_a_bundle(tmp_path, songs):
    folder, summary = bundle(tmp_path, songs, "first", strong())

    assert summary["best"] == "logistic_regression"
    assert summary["rows"]["train"] + summary["rows"]["validation"] + summary["rows"]["test"] == 600
    assert (folder / "logistic_regression" / "model.joblib").exists()
    assert (folder / "test.joblib").exists()
    assert set(summary["drift_reference"]) == {"/".join(key) for key in DRIFT_FEATURES}
    assert json.loads((folder / "summary.json").read_text())["task"] == "instrumental"


def test_the_gate_promotes_only_a_better_model(tmp_path, songs):
    registry = Registry(tmp_path / "ml")

    first, decision = registry.register(bundle(tmp_path, songs, "first", strong())[0])
    assert decision.promote
    assert registry.alias("instrumental", "champion").version == first

    second, decision = registry.register(bundle(tmp_path, songs, "second", weak())[0])
    assert not decision.promote
    assert registry.alias("instrumental", "champion").version == first
    assert registry.alias("instrumental", "challenger").version == second

    pointer = json.loads((tmp_path / "ml" / "models" / "instrumental" / "champion.json").read_text())
    meta = json.loads((tmp_path / "ml" / "models" / "instrumental" / f"v{first}" / "meta.json").read_text())
    assert pointer == {"version": int(first)}
    assert meta["candidate"] == "logistic_regression"
    assert meta["classes"] == ["0", "1"]
    assert "spectral_centroid/mean/01" in meta["drift_reference"]


def test_new_data_scores_the_champion_again_on_the_new_test_rows(tmp_path, songs):
    registry = Registry(tmp_path / "ml")
    first, _ = registry.register(bundle(tmp_path, songs, "first", strong())[0])
    grown = Dataset(songs.X, songs.y, songs.ids, "synthetic", "def456")
    loads = mock.patch.object(registry_module.mlflow.sklearn, "load_model",
                              wraps=registry_module.mlflow.sklearn.load_model)

    with loads as loaded:
        _, same = registry.register(bundle(tmp_path, songs, "same", weak())[0])
        same_data_loads = loaded.call_count
        train(TASK, grown, tmp_path / "new", weak())
        _, new = registry.register(tmp_path / "new")

    assert same_data_loads == 0
    assert any(call.args[0] == f"models:/instrumental/{first}" for call in loaded.call_args_list)
    assert not same.promote
    assert not new.promote


def test_a_chosen_candidate_can_be_registered_and_promoted_with_a_reason(tmp_path, songs):
    registry = Registry(tmp_path / "ml")
    best, _ = registry.register(bundle(tmp_path, songs, "first", strong())[0])
    chosen, decision = registry.register(tmp_path / "first", candidate="always_vocal")
    registry.promote("instrumental", chosen, reason="checked on our own songs")
    version = registry.client.get_model_version("instrumental", chosen)

    assert not decision.promote
    assert version.tags["candidate"] == "always_vocal"
    assert version.tags["promoted_because"] == "checked on our own songs"
    assert registry.alias("instrumental", "champion").version == chosen != best


def test_promote_rolls_back_to_an_older_version(tmp_path, songs):
    registry = Registry(tmp_path / "ml")
    first, _ = registry.register(bundle(tmp_path, songs, "first", strong())[0])
    second, _ = registry.register(bundle(tmp_path, songs, "second", weak())[0])

    folder = registry.promote("instrumental", second)

    assert registry.alias("instrumental", "champion").version == second
    assert json.loads((folder.parent / "champion.json").read_text()) == {"version": int(second)}
    assert first != second
