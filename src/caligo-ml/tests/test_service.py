import json
import math
import os
import shutil
import time
import uuid
from datetime import timedelta
from pathlib import Path

import joblib
import numpy as np
import pandas as pd
import pytest
import soundfile
from fastapi.testclient import TestClient
from PIL import Image
from sklearn.linear_model import LogisticRegression, Ridge
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from sqlalchemy import create_engine, event, func, insert, select, text
from tiny_onnx import GENRE_CLASSES, spectrogram_model

from caligo_ml.algorithms import WordRuleClassifier
from caligo_ml.datasets import GENRES
from caligo_ml.deep.onnx_models import OnnxSpectrogramClassifier
from caligo_ml.evaluation import reference_bins
from caligo_ml.features.audio import RATE
from caligo_ml.features.text import tfidf_words
from caligo_ml.service import jobs, scoring
from caligo_ml.service.app import app
from caligo_ml.service.store import comments, create_database, job_runs, predictions, problems, songs
from caligo_ml.tasks import TASKS
from caligo_ml.training import prediction_reference

DATA = Path(__file__).parent / "data"
ALBUM = "demo album"
POSTGRES = os.getenv("ML_TEST_DATABASE_URL", "").rstrip("/")


def database_url(folder, name):
    if POSTGRES:
        return f"{POSTGRES}/{name}_{uuid.uuid4().hex[:8]}"
    return f"sqlite:///{(folder / f'{name}.db').as_posix()}"


def publish(root, task_name, model, X_train, X_test, rules=None):
    task = TASKS[task_name]
    folder = root / "models" / task_name / "v1"
    folder.mkdir(parents=True)
    joblib.dump(model, folder / "model.joblib")
    if rules is not None:
        joblib.dump(rules, folder / "rules.joblib")
    meta = {
        "task": task_name, "version": 1, "candidate": "test", "metric": task.metric, "test": {task.metric: 0.9},
        "classes": [str(label) for label in getattr(model, "classes_", [])], "data_sha256": "test",
        "drift_reference": {name: reference_bins(values) for name, values in task.drift_profile(X_train).items()},
        "prediction_reference": prediction_reference(task, model, X_test),
    }
    (folder / "meta.json").write_text(json.dumps(meta), encoding="utf-8")
    (folder.parent / "champion.json").write_text(json.dumps({"version": 1}), encoding="utf-8")


@pytest.fixture(scope="module")
def fma():
    return np.load(DATA / "fma-sample" / "fma-sample.npz", allow_pickle=False)


@pytest.fixture(scope="module")
def models(tmp_path_factory, fma):
    root = tmp_path_factory.mktemp("ml")
    spam = pd.concat(pd.read_csv(path) for path in sorted((DATA / "youtube-spam").glob("*.csv")))
    texts, labels = spam.content.to_numpy(dtype=object), spam.spam.to_numpy()
    publish(root, "spam", make_pipeline(tfidf_words(), LogisticRegression(max_iter=1000)).fit(texts, labels),
            texts, texts[:300], rules=WordRuleClassifier(max_features=100, random_state=42).fit(texts, labels))

    genre_X, mood_X = fma["genre_X"].astype(np.float32), fma["mood_X"].astype(np.float32)
    instrumental = (fma["instrumentalness"] >= 0.5).astype(int)
    publish(root, "genre", make_pipeline(StandardScaler(), LogisticRegression(C=0.1, max_iter=2000))
            .fit(genre_X, fma["genre_y"]), genre_X, genre_X)
    publish(root, "mood", make_pipeline(StandardScaler(), Ridge(alpha=100)).fit(mood_X, fma["mood_y"]),
            mood_X, mood_X)
    publish(root, "instrumental", make_pipeline(StandardScaler(), LogisticRegression(C=0.1, max_iter=2000))
            .fit(mood_X, instrumental), mood_X, mood_X)
    rng = np.random.default_rng(5)
    mels = rng.normal(-40, 15, (40, 128, 431)).astype(np.float32)
    publish(root, "genre_cnn", OnnxSpectrogramClassifier(spectrogram_model(), GENRE_CLASSES, -40.0, 15.0),
            mels, mels)
    return root


def sound(seconds, pitch, seed):
    rng = np.random.default_rng(seed)
    t = np.arange(int(seconds * RATE)) / RATE
    beat = (np.sin(2 * np.pi * 2 * t) > 0.6).astype(float)
    return (0.3 * np.sin(2 * np.pi * pitch * t) + 0.1 * beat * rng.normal(size=len(t))).astype(np.float32)


@pytest.fixture(scope="module")
def music(tmp_path_factory):
    root = tmp_path_factory.mktemp("public_html")
    album = root / "mixtape" / ALBUM
    album.mkdir(parents=True)
    for name, samples in (("one.mp3", sound(8, 220, 1)), ("two.mp3", sound(8, 330, 2)),
                          ("three.mp3", sound(8, 440, 3)), ("quiet.mp3", np.zeros(8 * RATE, dtype=np.float32))):
        soundfile.write(album / name, samples, RATE, format="MP3")
    cover = Image.new("RGB", (40, 40), (200, 30, 30))
    cover.paste((20, 20, 20), (0, 0, 40, 20))
    cover.save(album / "cover.png")
    tracks = [{"file": "one.mp3", "title": "First"}, {"file": "two.mp3", "title": "Second"},
              {"file": "three.mp3", "title": "Third"}, {"file": "quiet.mp3", "title": "Silence"}]
    radio = root / "music"
    radio.mkdir()
    soundfile.write(radio / "first-again.mp3", sound(8, 220, 1), RATE, format="MP3")
    (radio / "playlist.json").write_text(json.dumps(
        [{"file": "first-again.mp3", "title": "First", "artist": "Demo Band"}]), encoding="utf-8")
    (album / "metadata.json").write_text(json.dumps({"albumTitle": "Demo", "albumArtist": "Demo Band",
                                                     "coverFile": "cover.png", "tracks": tracks}), encoding="utf-8")
    return root


@pytest.fixture(scope="module")
def client(models, music, tmp_path_factory):
    with pytest.MonkeyPatch.context() as patch:
        patch.setenv("ML_ROOT", str(models))
        patch.setenv("ML_DATABASE_URL", database_url(tmp_path_factory.mktemp("db"), "caligoml"))
        patch.setenv("ML_MUSIC_ROOT", str(music))
        patch.setenv("ML_SCHEDULE", "off")
        patch.delenv("ML_CATALOG_DATABASE_URL", raising=False)
        with TestClient(app) as test_client:
            yield test_client


def count(client, table):
    with client.app.state.ml.engine.connect() as connection:
        return connection.execute(select(func.count()).select_from(table)).scalar()


def catalog_with(folder, rows):
    if POSTGRES:
        url = database_url(folder, "catalog")
        create_database(url)
        engine = create_engine(url)
        with engine.begin() as connection:
            connection.execute(text("CREATE SCHEMA streamingservice"))
    else:
        engine = create_engine("sqlite://")

        @event.listens_for(engine, "connect")
        def attach(connection, _):
            connection.execute(f"ATTACH DATABASE '{(folder / 'catalog.db').as_posix()}' AS streamingservice")

    with engine.begin() as connection:
        connection.execute(text("CREATE TABLE streamingservice.comment "
                                "(comment_id INTEGER PRIMARY KEY, user_id INTEGER, content_id INTEGER, comment TEXT)"))
        for comment_id, body in rows:
            connection.execute(text("INSERT INTO streamingservice.comment VALUES (:id, 1, 5, :body)"),
                               {"id": comment_id, "body": body})
    return engine


def test_health_and_ready(client):
    assert client.get("/health").json() == {"status": "ok"}
    ready = client.get("/ready").json()

    assert ready["ready"] is True
    assert ready["models"] == {"genre": 1, "mood": 1, "instrumental": 1, "spam": 1, "genre_cnn": 1}


def test_moderation_flags_spam_and_says_why(client):
    response = client.post("/predict/moderation", json={"comments": [
        {"text": "Please subscribe to my channel http://example.com/free"}, {"text": "Her voice is beautiful"}]})
    spam, fine = response.json()["comments"]

    assert spam["flagged"] is True
    assert spam["spam"] > 0.5
    assert any(reason.startswith("spam: [") for reason in spam["reasons"])
    assert spam["toxicity"] is None
    assert fine["flagged"] is False
    assert fine["reasons"] == []


@pytest.mark.parametrize("body", [{}, {"comments": []}, {"comments": [{"text": ""}]},
                                  {"comments": [{"text": "x" * 5001}]}])
def test_bad_comments_are_rejected_and_counted(client, body):
    before = count(client, problems)

    assert client.post("/predict/moderation", json=body).status_code == 422
    assert count(client, problems) == before + 1


def test_a_song_gets_genre_mood_and_instrumental(client, fma):
    response = client.post("/predict/song", json={"songs": [{"features": fma["genre_X"][0].astype(float).tolist()}]})
    [song] = response.json()["songs"]

    assert song["genre"] in GENRES
    assert len(song["genres"]) == 3
    assert sum(entry["probability"] for entry in song["genres"]) <= 1.0001
    assert all(0 <= song[name] <= 1 for name in ("energy", "valence", "danceability", "instrumental"))


def test_bad_song_features_are_rejected(client, fma):
    features = fma["genre_X"][0].astype(float).tolist()
    broken = features.copy()
    broken[3] = math.nan

    assert client.post("/predict/song", json={"songs": [{"features": features[:-1]}]}).status_code == 422
    assert client.post("/predict/song", content=json.dumps({"songs": [{"features": broken}]}),
                       headers={"content-type": "application/json"}).status_code == 422


def test_every_prediction_is_logged(client, fma):
    before = count(client, predictions)
    client.post("/predict/moderation", json={"comments": [{"text": "great beat"}]})
    client.post("/predict/song", json={"songs": [{"features": fma["mood_X"][0].astype(float).tolist()}]})

    assert count(client, predictions) == before + 1 + len(scoring.AUDIO_TASKS)


def test_stored_comments_go_to_the_queue_until_labelled(client):
    stored = client.post("/moderation/comments", json={"comment_id": 7, "content_id": 3,
                                                       "text": "subscribe to my channel for free gift cards"})

    queued = client.get("/moderation/queue").json()["comments"]

    assert stored.json()["flagged"] is True
    assert [(row["comment_id"], row["text"]) for row in queued] == [(7, "subscribe to my channel for free gift cards")]
    assert client.post("/moderation/comments/7/label", json={"label": "maybe"}).status_code == 422
    assert client.post("/moderation/comments/99/label", json={"label": "spam"}).status_code == 404
    assert client.post("/moderation/comments/7/label", json={"label": "spam"}).status_code == 200
    assert client.get("/moderation/queue").json()["comments"] == []
    assert client.get("/monitoring").json()["moderation"] == {"labelled": 1, "precision": 1.0, "recall": 1.0}


def test_library_songs_are_scored_once_and_matched(client):
    state = client.app.state.ml
    detail = jobs.run(state, "score_songs")
    song = client.get("/songs", params={"uri": "/mixtape/demo%20album/one.mp3"}).json()

    assert detail == {"scored": 4, "problems": 1, "removed": 0, "remaining": 0}
    assert song["genre"] in GENRES
    assert state.library.source == "embedding"
    with state.engine.connect() as connection:
        stored = connection.execute(select(songs).where(songs.c.path == f"mixtape/{ALBUM}/one.mp3")).mappings().one()
    assert stored["genre_cnn"] in GENRES
    assert stored["genre_features"] in GENRES
    assert len(stored["embedding"]) == 6
    assert set(stored["spectrum"]) == {"low", "mid", "high"}
    assert (song["title"], song["artist"], song["album"]) == ("First", "Demo Band", "Demo")
    assert song["uri"] == "/mixtape/demo%20album/one.mp3"
    assert {(entry["title"], entry["uri"]) for entry in song["similar"]} == {
        ("Second", "/mixtape/demo%20album/two.mp3"), ("Third", "/mixtape/demo%20album/three.mp3")}
    assert client.get("/songs", params={"uri": f"/mixtape/{ALBUM}/quiet.mp3"}).status_code == 404
    assert client.get("/songs", params={"uri": "/mixtape/../secrets.mp3"}).status_code == 404
    assert jobs.run(state, "score_songs") == {"scored": 0, "problems": 0, "removed": 0, "remaining": 0}


def test_a_new_feature_model_reuses_the_stored_features(client, models, monkeypatch):
    state = client.app.state.ml
    folder = models / "models" / "genre"
    shutil.copytree(folder / "v1", folder / "v2")
    (folder / "champion.json").write_text(json.dumps({"version": 2}), encoding="utf-8")

    def no_audio(path):
        raise AssertionError(f"decoded {path}")

    monkeypatch.setattr(jobs.audio, "load_clip", no_audio)
    try:
        detail = jobs.run(state, "score_songs")
        with state.engine.connect() as connection:
            versions = [row.versions["genre"] for row in connection.execute(
                select(songs.c.versions).where(songs.c.problem.is_(None)))]
    finally:
        (folder / "champion.json").write_text(json.dumps({"version": 1}), encoding="utf-8")
        state.champions.refresh()

    assert detail == {"scored": 4, "problems": 0, "removed": 0, "remaining": 0}
    assert versions == [2, 2, 2, 2]


def test_genre_corrections_count_towards_accuracy(client):
    song = client.get("/songs", params={"uri": f"/mixtape/{ALBUM}/two.mp3"}).json()
    other = next(genre for genre in GENRES if genre != song["genre"])

    assert client.post("/songs/label", json={"uri": f"/mixtape/{ALBUM}/two.mp3", "genre": "Polka"}).status_code == 422
    assert client.post("/songs/label", json={"uri": f"/mixtape/{ALBUM}/two.mp3", "genre": other}).status_code == 200
    assert client.get("/monitoring").json()["genre"] == {"labelled": 1, "accuracy": 0.0}


def test_album_covers_get_a_palette(client):
    assert jobs.run(client.app.state.ml, "album_palettes") == {"albums": 1}
    palette = client.get("/albums", params={"folder": ALBUM}).json()

    assert palette["accent"] is not None
    assert {entry["color"] for entry in palette["colors"]} == {"#c81e1e", "#141414"}
    assert [song["title"] for song in palette["songs"]] == ["First", "Third", "Second"]
    assert all(song["genre"] in GENRES for song in palette["songs"])
    assert client.get("/albums", params={"folder": "nothing"}).status_code == 404


def test_the_backlog_scores_catalog_comments(client, tmp_path):
    state = client.app.state.ml
    state.catalog = catalog_with(tmp_path, [(1, "nice song"), (2, "subscribe to my channel now")])
    try:
        first = jobs.run(state, "moderate_backlog")
        second = jobs.run(state, "moderate_backlog")
    finally:
        state.catalog.dispose()
        state.catalog = None
    with state.engine.connect() as connection:
        flagged = dict(connection.execute(select(comments.c.comment_id, comments.c.flagged)).all())

    assert first == {"scored": 2, "removed": 1}
    assert second == {"scored": 0, "removed": 0}
    assert flagged == {1: False, 2: True}


def test_drift_report_compares_caligo_songs_with_training(client, fma):
    state = client.app.state.ml
    features = fma["mood_X"][:40].astype(np.float32)
    results = scoring.songs(state.champions, features)
    with state.engine.begin() as connection:
        for index, (values, result) in enumerate(zip(features, results, strict=True)):
            connection.execute(insert(songs).values(
                path=f"music/drift-{index}.mp3", analyzed_at=jobs.now(), file_size=1, modified=0.0,
                versions={}, features=values.astype(float).tolist(),
                **{name: result[name] for name in ("genre", "genres", "energy", "valence", "danceability",
                                                    "instrumental")}))
    detail = jobs.run(state, "drift_report")
    report = client.get("/monitoring").json()["drift"]

    assert detail["checks"] == len(report) > 0
    assert {row["task"] for row in report} >= {"genre", "mood", "instrumental"}
    assert {row["kind"] for row in report} == {"input", "prediction"}
    assert all(row["status"] in ("ok", "watch", "drift") for row in report)


def test_a_run_left_behind_by_a_killed_process_is_marked_abandoned(client):
    state = client.app.state.ml
    with state.engine.begin() as connection:
        connection.execute(insert(job_runs).values(job="album_palettes", started_at=jobs.now(), status="running"))
    jobs.run(state, "album_palettes")
    latest = (select(job_runs.c.status).where(job_runs.c.job == "album_palettes")
              .order_by(job_runs.c.id.desc()).limit(2))
    with state.engine.connect() as connection:
        statuses = [row.status for row in connection.execute(latest)]

    assert statuses == ["done", "abandoned"]


def test_a_job_can_be_started_over_http(client):
    before = len(client.get("/monitoring").json()["jobs"])

    assert client.post("/jobs/album_palettes").status_code == 202
    assert client.post("/jobs/nothing").status_code == 422
    for _ in range(50):
        if len(client.get("/monitoring").json()["jobs"]) > before:
            break
        time.sleep(0.1)
    assert client.get("/monitoring").json()["jobs"][0]["job"] == "album_palettes"


def test_monitoring_lists_models_and_jobs(client):
    monitoring = client.get("/monitoring").json()

    assert [model["task"] for model in monitoring["models"]] == ["genre", "genre_cnn", "instrumental", "mood", "spam"]
    assert {run["job"] for run in monitoring["jobs"]} >= {"score_songs", "album_palettes", "moderate_backlog"}
    assert monitoring["rejected"]["/predict/moderation"] >= 4


def test_the_flows_wrap_the_same_jobs(tmp_path, monkeypatch):
    monkeypatch.setenv("PREFECT_HOME", str(tmp_path))
    from caligo_ml import flows, retraining

    assert {flow.name for flow in (flows.score_songs, flows.album_palettes, flows.moderate_backlog,
                                   flows.drift_report, retraining.retrain)} == {
        "score-songs", "album-palettes", "moderate-backlog", "drift-report", "retrain"}


def test_predictions_are_compared_week_over_week(client):
    state = client.app.state.ml
    rng = np.random.default_rng(5)
    week = jobs.now() - timedelta(days=7)

    def logged(moment, index, output, task):
        return {"created_at": moment, "task": task, "model_version": 1, "source": "test",
                "subject": f"week-{task}-{moment.date()}-{index}", "profile": {}, "output": output, "latency_ms": 1.0}

    rows = [logged(week - timedelta(days=3), index, {"spam": float(score)}, "spam")
            for index, score in enumerate(rng.uniform(0.0, 0.3, 60))]
    rows += [logged(jobs.now() - timedelta(days=1), index, {"spam": float(score)}, "spam")
             for index, score in enumerate(rng.uniform(0.6, 1.0, 60))]
    rows += [logged(week - timedelta(days=3), index, {"genres": [{"genre": "Rock", "probability": 0.9}]}, "genre")
             for index in range(40)]
    rows += [logged(jobs.now() - timedelta(days=1), index, {"genres": [{"genre": genre, "probability": 0.9}]}, "genre")
             for index, genre in enumerate(["Pop"] * 30 + ["Rock"] * 10)]
    with state.engine.begin() as connection:
        connection.execute(insert(predictions), rows)
        checked = jobs.weekly_drift(connection, "spam") + jobs.weekly_drift(connection, "genre")
        report = {row["task"]: row for row in checked}
        quiet = jobs.weekly_drift(connection, "toxicity")
        connection.execute(predictions.delete().where(predictions.c.subject.like("week-%")))

    assert report["spam"]["kind"] == "prediction_week" and report["spam"]["status"] == "drift"
    assert report["spam"]["rows"] >= 60
    assert report["genre"]["name"] == "genre" and report["genre"]["status"] == "drift"
    assert quiet == []
