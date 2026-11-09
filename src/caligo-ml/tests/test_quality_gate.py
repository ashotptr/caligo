from pathlib import Path

import numpy as np
import pandas as pd
import pytest

from caligo_ml.evaluation import frozen_split
from caligo_ml.tasks import TASKS

DATA = Path(__file__).parent / "data"
SPAM_FLOOR = {
    "naive_bayes": 0.9,
    "bernoulli_naive_bayes": 0.9,
    "decision_tree": 0.85,
    "ripper": 0.85,
    "logistic_regression": 0.9,
}
AUDIO_FLOOR = {
    "genre": {"softmax_regression": 0.42, "two_layer_net": 0.4, "logistic_regression": 0.42, "svm": 0.42,
              "decision_tree": 0.22, "lightgbm": 0.44},
    "mood": {"linear_regression_gd": 0.25, "polynomial_regression": 0.24, "two_layer_net": 0.21, "lightgbm": 0.18},
    "instrumental": {"logistic_regression": 0.78, "polynomial_logistic_regression": 0.8, "svm_rbf": 0.83,
                     "softmax_regression": 0.77, "two_layer_net": 0.78},
}


@pytest.fixture(scope="module")
def unseen_video():
    videos = {path.stem: pd.read_csv(path) for path in sorted((DATA / "youtube-spam").glob("*.csv"))}
    test = videos.pop("shakira")
    train = pd.concat(videos.values(), ignore_index=True)
    return train.content, train.spam, test.content, test.spam


@pytest.fixture(scope="module")
def fma():
    data = np.load(DATA / "fma-sample" / "fma-sample.npz", allow_pickle=False)
    instrumental = (data["instrumentalness"] >= 0.5).astype(int)
    return {
        "genre": (data["genre_X"].astype(np.float32), data["genre_y"], data["genre_ids"]),
        "mood": (data["mood_X"].astype(np.float32), data["mood_y"], data["mood_ids"]),
        "instrumental": (data["mood_X"].astype(np.float32), instrumental, data["mood_ids"]),
    }


@pytest.mark.parametrize("name", list(SPAM_FLOOR))
def test_spam_filter_on_comments_from_an_unseen_video(name, unseen_video):
    X_train, y_train, X_test, y_test = unseen_video
    task = TASKS["spam"]
    metrics = task.evaluate(task.candidates()[name].fit(X_train, y_train), X_test, y_test)
    print(f"spam {name:22} PR-AUC {metrics['pr_auc']:.3f}  F1 {metrics['f1']:.3f}  floor {SPAM_FLOOR[name]}")

    assert metrics["pr_auc"] >= SPAM_FLOOR[name]


AUDIO_CASES = [(task, name) for task, floors in AUDIO_FLOOR.items() for name in floors]


@pytest.mark.parametrize(("task_name", "name"), AUDIO_CASES)
def test_audio_model_on_real_fma_features(task_name, name, fma):
    X, y, ids = fma[task_name]
    task = TASKS[task_name]
    test = frozen_split(ids) == "test"
    score = task.evaluate(task.candidates()[name].fit(X[~test], y[~test]), X[test], y[test])[task.metric]
    floor = AUDIO_FLOOR[task_name][name]
    print(f"{task_name} {name:30} {task.metric} {score:.3f}  {'floor' if task.higher_is_better else 'ceiling'} {floor}")

    assert score >= floor if task.higher_is_better else score <= floor
