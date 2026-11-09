import asyncio
import json
import logging
import time
from collections import Counter
from contextlib import contextmanager
from datetime import UTC, datetime, timedelta

import numpy as np
from sqlalchemy import delete, insert, select, text, update
from sqlalchemy.dialects.postgresql import insert as postgres_insert
from sqlalchemy.dialects.sqlite import insert as sqlite_insert

from caligo_ml.datasets import MOODS
from caligo_ml.evaluation import category_psi, drift_status, psi, reference_bins
from caligo_ml.features import audio, covers
from caligo_ml.service import scoring, telemetry
from caligo_ml.service.store import albums, comments, drift, job_runs, predictions, problems, songs

log = logging.getLogger("caligo-ml")
SONGS_PER_RUN = 50
INTERVALS = {"score_songs": 3600, "album_palettes": 6 * 3600, "moderate_backlog": 600, "drift_report": 24 * 3600}
MINIMUM_SONGS_FOR_DRIFT = 30


def now():
    return datetime.now(UTC)


def upsert(connection, table, values, keys):
    build = postgres_insert if connection.dialect.name == "postgresql" else sqlite_insert
    statement = build(table).values(values)
    changed = {column: statement.excluded[column] for column in values if column not in keys}
    connection.execute(statement.on_conflict_do_update(index_elements=keys, set_=changed))


@contextmanager
def exclusive(connection, name):
    if connection.dialect.name != "postgresql":
        yield True
        return
    taken = connection.execute(text("SELECT pg_try_advisory_lock(hashtext(:name))"), {"name": name}).scalar()
    connection.commit()
    try:
        yield taken
    finally:
        if taken:
            connection.execute(text("SELECT pg_advisory_unlock(hashtext(:name))"), {"name": name})
            connection.commit()


def library_files(music_root):
    if music_root is None or not music_root.exists():
        return {}
    files = sorted(music_root.glob("mixtape/*/*.mp3")) + sorted(music_root.glob("music/*.mp3"))
    return {path.relative_to(music_root).as_posix(): path for path in files}


def library_titles(music_root):
    titles = {}
    if music_root is None or not music_root.exists():
        return titles
    for metadata in sorted(music_root.glob("mixtape/*/metadata.json")):
        try:
            album = json.loads(metadata.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        for track in album.get("tracks") or []:
            if isinstance(track, dict) and track.get("file"):
                titles[f"mixtape/{metadata.parent.name}/{track['file']}"] = (
                    track.get("title"), album.get("albumArtist"), album.get("albumTitle"))
    playlist = music_root / "music" / "playlist.json"
    try:
        entries = json.loads(playlist.read_text(encoding="utf-8")) if playlist.exists() else []
    except (OSError, ValueError):
        entries = []
    for entry in entries if isinstance(entries, list) else []:
        if isinstance(entry, dict) and entry.get("file"):
            titles[f"music/{entry['file']}"] = (entry.get("title"), entry.get("artist"), entry.get("album"))
    return titles


def name_of(titles, key):
    title, artist, album = titles.get(key, (None, None, None))
    fallback = key.rsplit("/", 1)[-1].rsplit(".", 1)[0]
    return {"title": (title or fallback)[:255], "artist": artist[:255] if artist else None,
            "album": album[:255] if album else None}


def changed(row, path):
    stat = path.stat()
    return row is None or row.file_size != stat.st_size or abs(row.modified - stat.st_mtime) > 1


def stored_sound_fits(row, versions):
    if row is None or row.problem is not None or row.features is None:
        return False
    if "genre_cnn" not in versions:
        return True
    return row.versions.get("genre_cnn") == versions["genre_cnn"] and row.cnn_chances is not None


def stale(row, path, versions):
    if changed(row, path):
        return True
    if row.problem is not None:
        return row.analyzed_at.replace(tzinfo=row.analyzed_at.tzinfo or UTC) < now() - timedelta(days=1)
    return row.versions != versions


def score_songs(state, limit=SONGS_PER_RUN):
    versions = state.champions.versions(scoring.SONG_TASKS)
    files = library_files(state.settings.music_root)
    if not versions:
        return {"scored": 0, "remaining": 0, "note": "no audio models"}
    titles = library_titles(state.settings.music_root)

    with state.engine.begin() as connection:
        known = {row.path: row for row in connection.execute(
            select(songs.c.path, songs.c.title, songs.c.artist, songs.c.album, songs.c.file_size,
                   songs.c.modified, songs.c.versions, songs.c.problem, songs.c.analyzed_at, songs.c.features,
                   songs.c.cnn_chances))}
        removed = sorted(set(known) - set(files))
        if removed:
            connection.execute(delete(songs).where(songs.c.path.in_(removed)))
        for key, row in known.items():
            names = name_of(titles, key)
            if key in files and (row.title, row.artist, row.album) != tuple(names.values()):
                connection.execute(update(songs).where(songs.c.path == key).values(**names))

    pending = [key for key, path in files.items() if stale(known.get(key), path, versions)]
    quick = [key for key in pending if not changed(known.get(key), files[key])
             and stored_sound_fits(known.get(key), versions)]
    for key in quick:
        row = known[key]
        started = time.perf_counter()
        values = np.asarray(row.features, dtype=np.float32)
        heard = [row.cnn_chances] if row.cnn_chances is not None else None
        [result] = scoring.songs(state.champions, values[None], heard=heard)
        update_row = {name: result.get(name) for name in ("genre", "genres", "energy", "valence", "danceability",
                                                          "instrumental")}
        update_row["genre_features"] = result["genres_features"][0]["genre"] if result.get("genres_features") else None
        with state.engine.begin() as connection:
            connection.execute(update(songs).where(songs.c.path == key)
                               .values(**update_row, versions=versions, analyzed_at=now()))
            scoring.log(connection, state.champions, scoring.AUDIO_TASKS, scoring.profiles("audio", values[None]),
                        [result], "batch", [key], started)
    pending = [key for key in pending if key not in set(quick)]
    heard, failed = 0, 0
    for key in pending[:limit]:
        path = files[key]
        stat = path.stat()
        row = {"path": key, **name_of(titles, key), "analyzed_at": now(), "file_size": stat.st_size,
               "modified": stat.st_mtime, "versions": versions, "problem": None, "features": None}
        started = time.perf_counter()
        try:
            clip = audio.load_clip(str(path))
            row["problem"] = audio.problem(clip)
        except Exception as error:
            row["problem"] = f"unreadable {type(error).__name__}"[:32]
        values = mels = None
        if row["problem"] is None:
            values = audio.features(clip).to_numpy(dtype=np.float32)
            if not np.isfinite(values).all():
                row["problem"] = "features not finite"
            elif state.champions.get("genre_cnn") is not None:
                mels = [audio.log_mel(clip)]

        with state.engine.begin() as connection:
            if row["problem"] is not None:
                failed += 1
                connection.execute(insert(problems), {"created_at": now(), "source": "score_songs",
                                                      "problem": f"{key}: {row['problem']}"[:256]})
                upsert(connection, songs, row, ["path"])
                continue
            [result] = scoring.songs(state.champions, values[None], mels)
            row.update({"features": [round(float(value), 6) for value in values], **{
                name: result.get(name) for name in ("genre", "genres", "energy", "valence", "danceability",
                                                     "instrumental", "genre_cnn", "spectrum", "embedding",
                                                     "cnn_chances")}})
            row["genre_features"] = result["genres_features"][0]["genre"] if result.get("genres_features") else None
            upsert(connection, songs, row, ["path"])
            scoring.log(connection, state.champions, scoring.AUDIO_TASKS, scoring.profiles("audio", values[None]),
                        [result], "batch", [key], started)
            if mels is not None:
                scoring.log(connection, state.champions, ("genre_cnn",), [result["spectrum"]], [result], "batch",
                            [key], started)
        heard += 1

    if heard or quick or removed:
        refresh_library(state)
    return {"scored": heard + len(quick), "problems": failed, "removed": len(removed),
            "remaining": len(pending) - heard - failed}


def refresh_library(state):
    with state.engine.begin() as connection:
        state.library.refresh(connection)
        grouped, report = state.library.groups()
        for path, group in grouped.items():
            connection.execute(update(songs).where(songs.c.path == path).values(sound_group=group))
    return report


def album_palettes(state):
    root = state.settings.music_root
    if root is None:
        return {"albums": 0}
    done = 0
    for metadata in sorted(root.glob("mixtape/*/metadata.json")):
        cover = json.loads(metadata.read_text(encoding="utf-8")).get("coverFile")
        path = metadata.parent / cover if cover else None
        if path is None or path.name != cover or not path.exists():
            continue
        colours = covers.palette(path)
        with state.engine.begin() as connection:
            upsert(connection, albums, {"folder": metadata.parent.name, "cover": cover,
                                        "colors": [{"color": colour, "share": share} for colour, share in colours],
                                        "accent": covers.accent(colours), "updated_at": now()}, ["folder"])
        done += 1
    return {"albums": done}


def score_comment(state, comment_id, content_id, body, source):
    started = time.perf_counter()
    [result] = scoring.comments(state.champions, [body])
    with state.engine.begin() as connection:
        existing = connection.execute(select(comments.c.label, comments.c.labelled_at)
                                      .where(comments.c.comment_id == comment_id)).first()
        upsert(connection, comments, {
            "comment_id": comment_id, "content_id": content_id, "text": body, "scored_at": now(),
            "spam": result["spam"],
            "toxicity": result["toxicity"], "flagged": result["flagged"], "reasons": result["reasons"],
            "versions": state.champions.versions(scoring.TEXT_TASKS),
            "label": existing.label if existing else None,
            "labelled_at": existing.labelled_at if existing else None,
        }, ["comment_id"])
        scoring.log(connection, state.champions, scoring.TEXT_TASKS, scoring.profiles("text", [body]), [result],
                    source, [str(comment_id)], started)
    return result


def catalog_comments(catalog, wanted=None):
    with catalog.connect() as connection:
        if connection.dialect.name == "postgresql":
            connection.execute(text("SET TRANSACTION READ ONLY"))
        if wanted is None:
            return {row.comment_id for row in connection.execute(
                text("SELECT comment_id FROM streamingservice.comment"))}
        rows = connection.execute(
            text("SELECT comment_id, content_id, comment FROM streamingservice.comment "
                 "WHERE comment_id IN (" + ", ".join(str(int(key)) for key in wanted) + ")"))
        return [(row.comment_id, row.content_id, row.comment) for row in rows]


def moderate_backlog(state, limit=200):
    versions = state.champions.versions(scoring.TEXT_TASKS)
    if not versions or state.catalog is None:
        return {"scored": 0, "note": "no moderation models or no catalog"}
    present = catalog_comments(state.catalog)
    with state.engine.begin() as connection:
        scored = {row.comment_id: row.versions for row in connection.execute(
            select(comments.c.comment_id, comments.c.versions))}
        gone = sorted(set(scored) - present)
        if gone:
            connection.execute(delete(comments).where(comments.c.comment_id.in_(gone)))
    pending = sorted(key for key in present if scored.get(key) != versions)[:limit]
    for comment_id, content_id, body in catalog_comments(state.catalog, pending) if pending else []:
        score_comment(state, comment_id, content_id, body, "batch")
    return {"scored": len(pending), "removed": len(gone)}


def drift_rows(task, kind, reference, current, rows):
    report = []
    for name, values in current.items():
        value = psi(reference[name], values)
        report.append({"task": task, "kind": kind, "name": name, "psi": round(value, 4),
                       "status": drift_status(value), "rows": rows})
    return report


def predicted(task, output):
    if task in ("genre", "genre_cnn"):
        genres = output.get("genres") or []
        return {"genre": genres[0]["genre"] if genres else None}
    if task == "mood":
        return {name: output.get(name) for name in MOODS}
    return {"score": output.get(task)}


def weekly_drift(connection, task):
    week = now() - timedelta(days=7)
    windows = []
    for start, stop in ((week - timedelta(days=7), week), (week, None)):
        chosen = select(predictions.c.output).where(predictions.c.task == task, predictions.c.created_at >= start)
        if stop is not None:
            chosen = chosen.where(predictions.c.created_at < stop)
        windows.append([predicted(task, row.output) for row in connection.execute(chosen)])
    before, recent = windows
    if min(len(before), len(recent)) < MINIMUM_SONGS_FOR_DRIFT:
        return []
    report = []
    for name in recent[0]:
        old = [item[name] for item in before if item[name] is not None]
        new = [item[name] for item in recent if item[name] is not None]
        if len(old) < MINIMUM_SONGS_FOR_DRIFT or len(new) < MINIMUM_SONGS_FOR_DRIFT:
            continue
        value = category_psi(Counter(old), Counter(new)) if name == "genre" else psi(reference_bins(old), new)
        report.append({"task": task, "kind": "prediction_week", "name": name, "psi": round(value, 4),
                       "status": drift_status(value), "rows": len(new)})
    return report


def drift_report(state):
    report = []
    with state.engine.connect() as connection:
        analysed = connection.execute(select(songs).where(songs.c.problem.is_(None),
                                                          songs.c.features.is_not(None))).all()
        heard = [row for row in analysed if row.spectrum is not None]
        cnn = state.champions.get("genre_cnn")
        if cnn is not None and len(heard) >= MINIMUM_SONGS_FOR_DRIFT:
            bands = {name: [row.spectrum[name] for row in heard] for name in cnn.meta["drift_reference"]}
            report += drift_rows("genre_cnn", "input", cnn.meta["drift_reference"], bands, len(heard))
            value = category_psi(cnn.meta["prediction_reference"]["counts"], Counter(row.genre_cnn for row in heard))
            report.append({"task": "genre_cnn", "kind": "prediction", "name": "genre", "psi": round(value, 4),
                           "status": drift_status(value), "rows": len(heard)})
        if len(analysed) >= MINIMUM_SONGS_FOR_DRIFT:
            features = np.array([row.features for row in analysed], dtype=float)
            for task in scoring.AUDIO_TASKS:
                champion = state.champions.get(task)
                if champion is None:
                    continue
                report += drift_rows(task, "input", champion.meta["drift_reference"],
                                     audio.drift_profile(features), len(analysed))
                expected = champion.meta["prediction_reference"]
                if task == "genre":
                    value = category_psi(expected["counts"], Counter(row.genre_features or row.genre
                                                                     for row in analysed))
                    report.append({"task": task, "kind": "prediction", "name": "genre", "psi": round(value, 4),
                                   "status": drift_status(value), "rows": len(analysed)})
                elif task == "instrumental":
                    report += drift_rows(task, "prediction", {"score": expected["score"]},
                                         {"score": [row.instrumental for row in analysed]}, len(analysed))
                else:
                    current = {f"output_{index}": [getattr(row, name) for row in analysed]
                               for index, name in enumerate(("energy", "valence", "danceability"))}
                    report += drift_rows(task, "prediction", expected, current, len(analysed))

        since = now() - timedelta(days=30)
        for task in scoring.TEXT_TASKS:
            champion = state.champions.get(task)
            if champion is None:
                continue
            logged = connection.execute(select(predictions.c.profile, predictions.c.output).where(
                predictions.c.task == task, predictions.c.created_at >= since)).all()
            if len(logged) < MINIMUM_SONGS_FOR_DRIFT:
                continue
            current = {name: [row.profile[name] for row in logged] for name in champion.meta["drift_reference"]}
            report += drift_rows(task, "input", champion.meta["drift_reference"], current, len(logged))
            report += drift_rows(task, "prediction", {"score": champion.meta["prediction_reference"]["score"]},
                                 {"score": [row.output[task] for row in logged]}, len(logged))

        for task in sorted(state.champions.versions()):
            report += weekly_drift(connection, task)

    if report:
        created = now()
        with state.engine.begin() as connection:
            connection.execute(insert(drift), [{"created_at": created, **row} for row in report])
    statuses = Counter(row["status"] for row in report)
    retrain = sorted({row["task"] for row in report if row["status"] == "drift" and row["kind"] == "input"})
    return {"checks": len(report), **statuses, "retrain_suggested": retrain}


JOBS = {"score_songs": score_songs, "album_palettes": album_palettes, "moderate_backlog": moderate_backlog,
        "drift_report": drift_report}


def run(state, name):
    with state.engine.connect() as holder, exclusive(holder, f"caligo-ml:{name}") as taken:
        if not taken:
            return {"skipped": "already running"}
        with state.engine.begin() as connection:
            connection.execute(update(job_runs).where(job_runs.c.job == name, job_runs.c.status == "running")
                               .values(status="abandoned"))
            run_id = connection.execute(insert(job_runs).values(job=name, started_at=now(), status="running")
                                        .returning(job_runs.c.id)).scalar()
        try:
            state.champions.refresh()
            detail, status = JOBS[name](state), "done"
        except Exception as error:
            log.exception("job %s failed", name)
            detail, status = {"error": f"{type(error).__name__}: {error}"[:500]}, "failed"
        with state.engine.begin() as connection:
            connection.execute(update(job_runs).where(job_runs.c.id == run_id)
                               .values(finished_at=now(), status=status, detail=detail))
        telemetry.job_runs.add(1, {"job": name, "outcome": status})
        log.info("job %s %s: %s", name, status, detail)
        return detail


async def schedule(state, startup_delay=60, tick=30):
    due = {name: time.monotonic() + startup_delay for name in JOBS}
    while True:
        for name in JOBS:
            if time.monotonic() >= due[name]:
                detail = await asyncio.to_thread(run, state, name)
                again = 60 if name == "score_songs" and detail.get("remaining") else INTERVALS[name]
                due[name] = time.monotonic() + again
        await asyncio.sleep(tick)
