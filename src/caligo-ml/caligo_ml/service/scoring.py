import time
from datetime import UTC, datetime

import numpy as np
from sqlalchemy import insert

from caligo_ml.datasets import MOODS
from caligo_ml.features import audio, text
from caligo_ml.service import telemetry
from caligo_ml.service.store import predictions

AUDIO_TASKS = ("genre", "mood", "instrumental")
SONG_TASKS = (*AUDIO_TASKS, "genre_cnn")
TEXT_TASKS = ("spam", "toxicity")
RULE_REQUIRED = ("spam",)
THRESHOLD = 0.5


def positive(model, X):
    return model.predict_proba(X)[:, list(model.classes_).index(1)]


def comments(champions, texts):
    texts = list(texts)
    results = [{"spam": None, "toxicity": None, "flagged": False, "reasons": []} for _ in texts]
    for task in TEXT_TASKS:
        champion = champions.get(task)
        if champion is None:
            continue
        scores = positive(champion.model, texts)
        fired = champion.rules.reasons(texts) if champion.rules is not None else [[] for _ in texts]
        for result, score, rules in zip(results, scores, fired, strict=True):
            result[task] = round(float(score), 4)
            needs_rule = task in RULE_REQUIRED and champion.rules is not None
            if score >= THRESHOLD and (rules or not needs_rule):
                result["flagged"] = True
                result["reasons"].extend(f"{task}: {rule}" for rule in rules)
    return results


def ranked(chances):
    order = sorted(chances, key=chances.get, reverse=True)[:3]
    return [{"genre": genre, "probability": round(float(chances[genre]), 4)} for genre in order]


def blend(*opinions):
    present = [opinion for opinion in opinions if opinion]
    if not present:
        return {}
    labels = sorted({label for opinion in present for label in opinion})
    return {label: sum(opinion.get(label, 0.0) for opinion in present) / len(present) for label in labels}


def opinions(model, chances):
    labels = [str(label) for label in model.classes_]
    return [dict(zip(labels, map(float, row), strict=True)) for row in chances]


def songs(champions, X, mels=None, heard=None):
    X = np.asarray(X, dtype=np.float32).reshape(-1, len(audio.columns()))
    results = [{} for _ in range(len(X))]

    genre = champions.get("genre")
    by_features = opinions(genre.model, genre.model.predict_proba(X)) if genre is not None else [{}] * len(X)
    cnn = champions.get("genre_cnn") if mels is not None else None
    by_sound = heard if heard is not None else [{}] * len(X)
    if cnn is not None:
        chances, embeddings = cnn.model.run(mels)
        by_sound = opinions(cnn.model, chances)
        bands = audio.spectrogram_profile(np.stack([audio.fit_frames(mel) for mel in mels]))
        for index, result in enumerate(results):
            result["cnn_chances"] = {label: round(value, 5) for label, value in by_sound[index].items()}
            result["genre_cnn"] = max(by_sound[index], key=by_sound[index].get)
            result["genres_cnn"] = ranked(by_sound[index])
            result["embedding"] = [round(float(value), 5) for value in embeddings[index]]
            result["spectrum"] = {name: float(values[index]) for name, values in bands.items()}
    for result, features, sound in zip(results, by_features, by_sound, strict=True):
        if features:
            result["genres_features"] = ranked(features)
        combined = blend(features, sound)
        if combined:
            result["genres"] = ranked(combined)
            result["genre"] = result["genres"][0]["genre"]

    mood = champions.get("mood")
    if mood is not None:
        values = np.clip(np.asarray(mood.model.predict(X), dtype=float).reshape(len(X), -1), 0, 1)
        for result, row in zip(results, values, strict=True):
            result.update({name: round(float(value), 4) for name, value in zip(MOODS, row, strict=True)})

    instrumental = champions.get("instrumental")
    if instrumental is not None:
        for result, chance in zip(results, positive(instrumental.model, X), strict=True):
            result["instrumental"] = round(float(chance), 4)

    return results


def task_output(task, result):
    if task == "genre":
        return {"genres": result.get("genres_features")}
    if task == "genre_cnn":
        return {"genres": result.get("genres_cnn")}
    if task == "mood":
        return {name: result.get(name) for name in MOODS}
    return {task: result.get(task)}


def log(connection, champions, tasks, profiles, results, source, subjects, started):
    latency = (time.perf_counter() - started) * 1000 / max(len(results), 1)
    created = datetime.now(UTC)
    rows = []
    for task in tasks:
        champion = champions.get(task)
        if champion is None:
            continue
        for profile, result, subject in zip(profiles, results, subjects, strict=True):
            rows.append({"created_at": created, "task": task, "model_version": champion.version,
                         "source": source, "subject": subject,
                         "profile": profile, "output": task_output(task, result), "latency_ms": round(latency, 2)})
    if rows:
        connection.execute(insert(predictions), rows)
        for task in tasks:
            telemetry.predictions.add(sum(row["task"] == task for row in rows), {"task": task, "source": source})


def profiles(kind, values):
    columns = audio.drift_profile(values) if kind == "audio" else text.drift_profile(values)
    count = len(next(iter(columns.values())))
    return [{name: float(column[index]) for name, column in columns.items()} for index in range(count)]
