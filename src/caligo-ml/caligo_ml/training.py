import json
import platform
import time
from collections import Counter
from importlib.metadata import version
from pathlib import Path

import joblib
import numpy as np
from sklearn.model_selection import train_test_split

from caligo_ml.evaluation import frozen_split, reference_bins
from caligo_ml.tasks import ROW_LIMITS, SEED

PACKAGES = ["numpy", "scipy", "pandas", "scikit-learn", "lightgbm", "wittgenstein", "librosa", "joblib"]
SIMPLE = (bool, int, float, str, type(None))


def environment():
    return {"python": platform.python_version(), **{name: version(name) for name in PACKAGES}}


def limit_rows(task, rows, y, limit):
    if limit is None or len(rows) <= limit:
        return rows
    stratify = None if task.kind == "regression" else y[rows]
    kept, _ = train_test_split(rows, train_size=limit, stratify=stratify, random_state=SEED)
    return np.sort(kept)


def params_of(model):
    final = model.best_estimator_ if hasattr(model, "best_estimator_") else model
    final = final.steps[-1][1] if hasattr(final, "steps") else final
    params = {}
    for owner in (final, getattr(final, "estimator", None)):
        if hasattr(owner, "get_params"):
            simple = {key: value for key, value in owner.get_params(deep=False).items() if isinstance(value, SIMPLE)}
            params.update(simple)
    return params


def prediction_reference(task, model, X):
    if task.kind == "multiclass":
        return {"counts": {str(label): count for label, count in Counter(model.predict(X)).items()}}
    if task.kind == "binary":
        return {"score": reference_bins(model.predict_proba(X)[:, list(model.classes_).index(1)])}
    predictions = np.asarray(model.predict(X)).reshape(len(X), -1)
    return {f"output_{index}": reference_bins(predictions[:, index]) for index in range(predictions.shape[1])}


def better(task, first, second):
    return first > second if task.higher_is_better else first < second


def best_candidate(task, results):
    best = results[0]
    for result in results[1:]:
        if better(task, result["validation"][task.metric], best["validation"][task.metric]):
            best = result
    return best


def train(task, data, out, candidates=None):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    split = frozen_split(data.ids)
    train_rows, validation, test = (np.flatnonzero(split == part) for part in ("train", "validation", "test"))
    X, y = data.X, data.y
    results = []

    for name, model in (candidates or task.candidates()).items():
        rows = limit_rows(task, train_rows, y, ROW_LIMITS.get(name))
        started = time.perf_counter()
        model.fit(X[rows], y[rows])
        seconds = time.perf_counter() - started
        result = {
            "candidate": name,
            "train_rows": int(len(rows)),
            "fit_seconds": round(seconds, 1),
            "params": params_of(model),
            "validation": task.evaluate(model, X[validation], y[validation]),
            "test": task.evaluate(model, X[test], y[test]),
            "predictions": prediction_reference(task, model, X[test]),
        }
        (out / name).mkdir(exist_ok=True)
        joblib.dump(model, out / name / "model.joblib")
        results.append(result)
        print(f"{task.name} {name:30} validation {task.metric} {result['validation'][task.metric]:.4f} | "
              f"test {result['test'][task.metric]:.4f} | {len(rows)} rows in {seconds:.1f}s", flush=True)

    joblib.dump({"X": X[test], "y": y[test], "ids": data.ids[test]}, out / "test.joblib")
    summary = {
        "task": task.name,
        "source": data.source,
        "sha256": data.sha256,
        "rows": {"train": int(len(train_rows)), "validation": int(len(validation)), "test": int(len(test))},
        "environment": environment(),
        "drift_reference": {name: reference_bins(values) for name, values in task.drift_profile(X[train_rows]).items()},
        "best": best_candidate(task, results)["candidate"],
        "results": results,
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2, default=str), encoding="utf-8")
    return summary
