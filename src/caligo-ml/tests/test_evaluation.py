import numpy as np
import pytest
from sklearn.datasets import make_blobs

from caligo_ml import evaluation
from caligo_ml.clustering import best_k, cluster_report, elbow_k, most_similar


def test_psi_tells_noise_from_drift():
    rng = np.random.default_rng(0)
    reference = evaluation.reference_bins(rng.normal(0, 1, 5000))

    assert evaluation.drift_status(evaluation.psi(reference, rng.normal(0, 1, 2000))) == "ok"
    assert evaluation.drift_status(evaluation.psi(reference, rng.normal(0.4, 1, 2000))) == "watch"
    assert evaluation.drift_status(evaluation.psi(reference, rng.normal(1, 1, 2000))) == "drift"


def test_psi_ignores_missing_values():
    reference = evaluation.reference_bins([1.0, 2.0, 3.0, 4.0, np.nan] * 100)

    assert evaluation.psi(reference, [1.0, 2.0, 3.0, 4.0, np.inf] * 50) == pytest.approx(0, abs=1e-6)


def test_category_psi_on_predicted_genres():
    last_week = {"Rock": 50, "Hip-Hop": 30, "Electronic": 20}

    assert evaluation.category_psi(last_week, {"Rock": 52, "Hip-Hop": 28, "Electronic": 20}) < evaluation.WATCH
    assert evaluation.category_psi(last_week, {"Rock": 10, "Hip-Hop": 30, "Folk": 60}) > evaluation.DRIFT


def test_the_frozen_split_never_moves_a_row():
    ids = [f"track-{number}" for number in range(10_000)]
    first = dict(zip(ids, evaluation.frozen_split(ids), strict=True))
    grown = ids[::-1] + [f"track-{number}" for number in range(10_000, 12_000)]
    second = dict(zip(grown, evaluation.frozen_split(grown), strict=True))
    shares = {part: np.mean([value == part for value in first.values()]) for part in ("train", "validation", "test")}

    assert shares["test"] == pytest.approx(0.2, abs=0.02)
    assert shares["validation"] == pytest.approx(0.16, abs=0.02)
    assert shares["train"] == pytest.approx(0.64, abs=0.02)
    assert all(second[key] == first[key] for key in ids)


@pytest.mark.parametrize(("challenger", "champion", "higher_is_better", "promote"), [
    (0.50, None, True, True),
    (0.40, None, True, False),
    (0.52, 0.50, True, True),
    (0.50, 0.50, True, False),
    (0.48, 0.50, True, False),
    (0.10, 0.12, False, True),
    (0.13, 0.12, False, False),
    (0.25, None, False, False),
])
def test_promotion_gate(challenger, champion, higher_is_better, promote):
    minimum = 0.45 if higher_is_better else 0.2
    decision = evaluation.promotion(challenger, champion, minimum, higher_is_better)

    assert decision.promote is promote
    assert decision.reason


def test_regression_metrics_adjust_r2_for_the_number_of_features():
    rng = np.random.default_rng(1)
    truth = rng.normal(size=200)
    metrics = evaluation.regression_metrics(truth, truth + rng.normal(0, 0.5, 200), n_features=10)

    assert metrics["adjusted_r2"] < metrics["r2"]
    assert metrics["mae"] == pytest.approx(0.4, abs=0.1)


def test_binary_metrics():
    metrics = evaluation.binary_metrics([0, 0, 1, 1], [0.1, 0.6, 0.4, 0.9])

    assert metrics["roc_auc"] == pytest.approx(0.75)
    assert metrics["precision"] == pytest.approx(0.5)
    assert metrics["recall"] == pytest.approx(0.5)


def test_k_means_finds_the_groups():
    X, _ = make_blobs(n_samples=400, centers=[[0, 0], [10, 0], [0, 10], [10, 10]], random_state=42)
    report = cluster_report(X, range(1, 9))

    assert best_k(report) == 4
    assert elbow_k(report) == 4
    assert report[0]["silhouette"] is None


def test_silhouette_decides_where_the_elbow_is_unclear():
    X, _ = make_blobs(n_samples=400, centers=4, cluster_std=0.7, random_state=42)
    report = cluster_report(X, range(1, 9))

    assert elbow_k(report) == 3
    assert best_k(report) == 4


def test_most_similar_leaves_the_item_out():
    vectors = np.array([[1, 0], [0.9, 0.1], [0, 1], [0.1, 0.9], [0.7, 0.7]])
    similar = most_similar(vectors, count=2)

    assert [row[0][0] for row in similar[:4]] == [1, 0, 3, 2]
    assert all(item not in [other for other, _ in row] for item, row in enumerate(similar))
    assert all(len(row) == 2 for row in similar)
