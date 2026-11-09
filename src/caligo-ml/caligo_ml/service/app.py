import asyncio
import contextlib
import logging
import time
from contextlib import asynccontextmanager
from datetime import timedelta
from typing import Annotated, Literal

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from sqlalchemy import func, insert, select, text, update

from caligo_ml.datasets import GENRES
from caligo_ml.features.audio import columns
from caligo_ml.service import jobs, scoring, telemetry
from caligo_ml.service.library import song_key, song_uri
from caligo_ml.service.state import State
from caligo_ml.service.store import albums, comments, drift, job_runs, predictions, problems, songs

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("caligo-ml")
FEATURES = len(columns())
Finite = Annotated[float, Field(allow_inf_nan=False)]


class Comment(BaseModel):
    text: str = Field(min_length=1, max_length=5000)


class Comments(BaseModel):
    comments: list[Comment] = Field(min_length=1, max_length=100)


class StoredComment(BaseModel):
    comment_id: int = Field(ge=1)
    content_id: int = Field(ge=1)
    text: str = Field(min_length=1, max_length=5000)


class Song(BaseModel):
    features: list[Finite] = Field(min_length=FEATURES, max_length=FEATURES)


class Songs(BaseModel):
    songs: list[Song] = Field(min_length=1, max_length=100)


class CommentLabel(BaseModel):
    label: Literal["spam", "toxic", "ok"]


class GenreLabel(BaseModel):
    uri: str = Field(min_length=1, max_length=600)
    genre: Literal[tuple(GENRES)]


@asynccontextmanager
async def lifespan(app):
    state = State.from_env()
    app.state.ml = state
    scheduler = asyncio.create_task(jobs.schedule(state)) if state.settings.schedule else None
    log.info("loaded models %s", state.champions.versions())
    yield
    if scheduler is not None:
        scheduler.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await scheduler
    state.close()
    telemetry.shutdown()


app = FastAPI(title="Caligo ML", lifespan=lifespan)
telemetry.instrument(app)


def ml(request):
    return request.app.state.ml


@app.exception_handler(RequestValidationError)
async def rejected(request: Request, error: RequestValidationError):
    errors = [{"loc": list(entry.get("loc", ())), "msg": entry.get("msg", "invalid"), "type": entry.get("type")}
              for entry in error.errors()]
    first = errors[0] if errors else {"loc": [], "msg": "invalid"}
    telemetry.rejected.add(1, {"route": request.url.path[:64]})
    with ml(request).engine.begin() as connection:
        connection.execute(insert(problems), {
            "created_at": jobs.now(),
            "source": request.url.path[:64],
            "problem": f"{'.'.join(str(part) for part in first['loc'])}: {first['msg']}"[:256]})
    return JSONResponse(status_code=422, content={"detail": errors})


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/ready")
def ready(request: Request):
    state = ml(request)
    try:
        with state.engine.connect() as connection:
            connection.execute(text("SELECT 1"))
    except Exception as error:
        raise HTTPException(503, f"database unavailable: {type(error).__name__}") from error
    return {"ready": True, "models": state.champions.versions()}


@app.post("/models/reload")
def reload(request: Request):
    return {"models": ml(request).champions.refresh().versions()}


@app.post("/jobs/{name}", status_code=202)
async def start_job(name: Literal[tuple(jobs.JOBS)], request: Request):
    running = request.app.state.__dict__.setdefault("started_jobs", set())
    task = asyncio.create_task(asyncio.to_thread(jobs.run, ml(request), name))
    running.add(task)
    task.add_done_callback(running.discard)
    return {"started": name}


@app.post("/predict/moderation")
def predict_moderation(body: Comments, request: Request):
    state = ml(request)
    started = time.perf_counter()
    texts = [comment.text for comment in body.comments]
    results = scoring.comments(state.champions, texts)
    with state.engine.begin() as connection:
        scoring.log(connection, state.champions, scoring.TEXT_TASKS, scoring.profiles("text", texts), results,
                    request.headers.get("x-caligo-source", "online")[:16], [None] * len(texts), started)
    return {"models": state.champions.versions(scoring.TEXT_TASKS), "comments": results}


@app.post("/predict/song")
def predict_song(body: Songs, request: Request):
    state = ml(request)
    started = time.perf_counter()
    values = [song.features for song in body.songs]
    results = scoring.songs(state.champions, values)
    with state.engine.begin() as connection:
        scoring.log(connection, state.champions, scoring.AUDIO_TASKS, scoring.profiles("audio", values), results,
                    request.headers.get("x-caligo-source", "online")[:16], [None] * len(values), started)
    return {"models": state.champions.versions(scoring.AUDIO_TASKS), "songs": results}


@app.post("/moderation/comments")
def moderate(body: StoredComment, request: Request):
    return jobs.score_comment(ml(request), body.comment_id, body.content_id, body.text, "online")


@app.get("/moderation/queue")
def queue(request: Request, limit: int = Query(50, ge=1, le=200)):
    with ml(request).engine.connect() as connection:
        rows = connection.execute(select(comments).where(comments.c.flagged.is_(True), comments.c.label.is_(None))
                                  .order_by(comments.c.scored_at.desc()).limit(limit)).mappings().all()
    return {"comments": [dict(row) for row in rows]}


@app.post("/moderation/comments/{comment_id}/label")
def label_comment(comment_id: int, body: CommentLabel, request: Request):
    with ml(request).engine.begin() as connection:
        changed = connection.execute(update(comments).where(comments.c.comment_id == comment_id)
                                     .values(label=body.label, labelled_at=jobs.now())).rowcount
    if not changed:
        raise HTTPException(404, "This comment has not been scored.")
    return {"comment_id": comment_id, "label": body.label}


def song_row(connection, uri):
    path = song_key(uri)
    row = connection.execute(select(songs).where(songs.c.path == path)).mappings().first() if path else None
    if row is None or row["problem"] is not None:
        raise HTTPException(404, "No analysis for this song.")
    return row


SONG_FIELDS = ("path", "title", "artist", "album", "genre", "genres", "genre_label", "energy", "valence",
               "danceability", "instrumental", "sound_group")


def song_view(row):
    return {**{name: row[name] for name in SONG_FIELDS}, "uri": song_uri(row["path"])}


@app.get("/songs")
def song(request: Request, uri: str = Query(min_length=1, max_length=600)):
    state = ml(request)
    with state.engine.connect() as connection:
        row = song_row(connection, uri)
    return {**song_view(row), "similar": state.library.similar.get(row["path"], [])}


@app.post("/songs/label")
def label_song(body: GenreLabel, request: Request):
    with ml(request).engine.begin() as connection:
        row = song_row(connection, body.uri)
        connection.execute(update(songs).where(songs.c.path == row["path"])
                           .values(genre_label=body.genre, labelled_at=jobs.now()))
    return {"path": row["path"], "genre": body.genre}


@app.get("/albums")
def album(request: Request, folder: str = Query(min_length=1, max_length=255)):
    with ml(request).engine.connect() as connection:
        palette = connection.execute(select(albums).where(albums.c.folder == folder)).mappings().first()
        tracks = connection.execute(
            select(songs).where(songs.c.path.startswith(f"mixtape/{folder}/", autoescape=True),
                                songs.c.problem.is_(None)).order_by(songs.c.path)).mappings().all()
    if palette is None and not tracks:
        raise HTTPException(404, "Nothing is known about this album.")
    return {"folder": folder, "colors": palette["colors"] if palette else [],
            "accent": palette["accent"] if palette else None, "songs": [song_view(row) for row in tracks]}


@app.get("/monitoring")
def monitoring(request: Request):
    state = ml(request)
    since = jobs.now() - timedelta(days=7)
    with state.engine.connect() as connection:
        latest = connection.execute(select(func.max(drift.c.created_at))).scalar()
        report = connection.execute(select(drift).where(drift.c.created_at == latest)).mappings().all() \
            if latest else []
        counts = connection.execute(select(predictions.c.task, predictions.c.source, func.count())
                                    .where(predictions.c.created_at >= since)
                                    .group_by(predictions.c.task, predictions.c.source)).all()
        rejected = connection.execute(select(problems.c.source, func.count()).where(problems.c.created_at >= since)
                                      .group_by(problems.c.source)).all()
        labelled = connection.execute(select(comments.c.label, comments.c.flagged)
                                      .where(comments.c.label.is_not(None))).all()
        corrected = connection.execute(select(songs.c.genre, songs.c.genre_label)
                                       .where(songs.c.genre_label.is_not(None))).all()
        runs = connection.execute(select(job_runs).order_by(job_runs.c.id.desc()).limit(20)).mappings().all()

    flagged = [row for row in labelled if row.flagged]
    confirmed = sum(row.label != "ok" for row in flagged)
    missed = sum(row.label != "ok" and not row.flagged for row in labelled)
    return {
        "models": [{"task": task, "version": champion.version, "candidate": champion.meta["candidate"],
                    "metric": champion.meta["metric"], "test": champion.meta["test"].get(champion.meta["metric"])}
                   for task, champion in sorted(state.champions.loaded.items())],
        "drift": [dict(row) for row in report],
        "predictions": [{"task": task, "source": source, "count": count} for task, source, count in counts],
        "rejected": {source: count for source, count in rejected},
        "moderation": {"labelled": len(labelled), "precision": round(confirmed / len(flagged), 4) if flagged else None,
                       "recall": round(confirmed / (confirmed + missed), 4) if confirmed + missed else None},
        "genre": {"labelled": len(corrected),
                  "accuracy": round(sum(row.genre == row.genre_label for row in corrected) / len(corrected), 4)
                  if corrected else None},
        "jobs": [dict(row) for row in runs],
    }
