import hashlib
from dataclasses import dataclass

import numpy as np
from sklearn.metrics import (
    accuracy_score,
    average_precision_score,
    confusion_matrix,
    f1_score,
    mean_absolute_error,
    mean_squared_error,
    precision_score,
    r2_score,
    recall_score,
    roc_auc_score,
)

WATCH = 0.1
DRIFT = 0.25


def classification_metrics(y_true, y_pred, labels):
    by_class = f1_score(y_true, y_pred, labels=labels, average=None, zero_division=0)
    return {
        "accuracy": float(accuracy_score(y_true, y_pred)),
        "macro_f1": float(f1_score(y_true, y_pred, labels=labels, average="macro", zero_division=0)),
        "f1_by_class": {str(label): round(float(score), 4) for label, score in zip(labels, by_class, strict=True)},
        "confusion_matrix": confusion_matrix(y_true, y_pred, labels=labels).tolist(),
    }


def binary_metrics(y_true, scores, threshold=0.5):
    flagged = np.asarray(scores) >= threshold
    return {
        "pr_auc": float(average_precision_score(y_true, scores)),
        "roc_auc": float(roc_auc_score(y_true, scores)),
        "precision": float(precision_score(y_true, flagged, zero_division=0)),
        "recall": float(recall_score(y_true, flagged, zero_division=0)),
        "f1": float(f1_score(y_true, flagged, zero_division=0)),
    }


def regression_metrics(y_true, y_pred, n_features):
    r2 = float(r2_score(y_true, y_pred))
    n = len(y_true)
    return {
        "mae": float(mean_absolute_error(y_true, y_pred)),
        "rmse": float(np.sqrt(mean_squared_error(y_true, y_pred))),
        "r2": r2,
        "adjusted_r2": 1 - (1 - r2) * (n - 1) / (n - n_features - 1),
    }


def finite(values):
    values = np.asarray(values, dtype=float)
    return values[np.isfinite(values)]


def reference_bins(values, count=10):
    values = finite(values)
    edges = np.unique(np.quantile(values, np.linspace(0, 1, count + 1)))[1:-1]
    share = np.bincount(np.searchsorted(edges, values, side="right"), minlength=len(edges) + 1) / len(values)
    return {"edges": edges.tolist(), "share": share.round(6).tolist()}


def stability(expected, actual):
    expected = np.clip(np.asarray(expected, dtype=float), 1e-4, None)
    actual = np.clip(np.asarray(actual, dtype=float), 1e-4, None)
    return float(np.sum((actual - expected) * np.log(actual / expected)))


def psi(reference, values):
    values = finite(values)
    actual = np.bincount(np.searchsorted(reference["edges"], values, side="right"),
                         minlength=len(reference["share"])) / len(values)
    return stability(reference["share"], actual)


def category_psi(expected_counts, actual_counts):
    labels = sorted(set(expected_counts) | set(actual_counts))
    expected = np.array([expected_counts.get(label, 0) for label in labels], dtype=float)
    actual = np.array([actual_counts.get(label, 0) for label in labels], dtype=float)
    return stability(expected / expected.sum(), actual / actual.sum())


def drift_status(value):
    return "drift" if value > DRIFT else "watch" if value > WATCH else "ok"


def frozen_split(ids, test_share=0.2, validation_share=0.16, salt="caligo-ml"):
    buckets = np.array([int.from_bytes(hashlib.sha256(f"{salt}:{key}".encode()).digest()[:8], "big") % 10_000
                        for key in ids])
    return np.where(buckets < test_share * 10_000, "test",
                    np.where(buckets < (test_share + validation_share) * 10_000, "validation", "train"))


@dataclass(frozen=True)
class Decision:
    promote: bool
    reason: str


def promotion(challenger, champion, minimum, higher_is_better=True, margin=0.0):
    sign = 1 if higher_is_better else -1
    if sign * (challenger - minimum) < 0:
        return Decision(False, f"challenger {challenger:.4f} misses the minimum {minimum:.4f}")
    if champion is None:
        return Decision(True, f"no champion yet, challenger {challenger:.4f} passes the minimum {minimum:.4f}")
    if sign * (challenger - champion) > margin:
        return Decision(True, f"challenger {challenger:.4f} beats the champion's {champion:.4f}")
    return Decision(False, f"challenger {challenger:.4f} does not beat the champion's {champion:.4f}")
