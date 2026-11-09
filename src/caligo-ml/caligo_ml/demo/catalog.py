import argparse
import json
from datetime import UTC, datetime
from pathlib import Path

from sqlalchemy import create_engine, select, text

from caligo_ml.service.library import Library, song_key
from caligo_ml.service.settings import Settings
from caligo_ml.service.store import songs

TITLES = text("""
SELECT c.content_id, c.title, c.duration, extract(year FROM c.release_date)::int AS year,
       CASE WHEN m.content_id IS NOT NULL THEN 'film' WHEN e.content_id IS NOT NULL THEN 'episode'
            WHEN s.content_id IS NOT NULL THEN 'song' END AS kind,
       sr.title AS series, e.season_number AS season, e.episode_number AS episode, s.audio_uri AS uri,
       (SELECT string_agg(g.name, '|' ORDER BY g.name) FROM streamingservice.contentgenres cg
          JOIN streamingservice.genres g ON g.genre_id = cg.genre_id WHERE cg.content_id = c.content_id) AS genres,
       (SELECT a.title FROM streamingservice.albumsong x JOIN streamingservice.album a ON a.album_id = x.album_id
          WHERE x.song_id = s.song_id ORDER BY x.album_id LIMIT 1) AS album,
       (SELECT x.disc_number * 1000 + x.track_number FROM streamingservice.albumsong x
          WHERE x.song_id = s.song_id ORDER BY x.album_id LIMIT 1) AS track,
       (SELECT string_agg(ar.name, ', ' ORDER BY ar.name) FROM streamingservice.songartist sa
          JOIN streamingservice.artist ar ON ar.artist_id = sa.artist_id WHERE sa.song_id = s.song_id) AS artist
FROM streamingservice.content c
LEFT JOIN streamingservice.movie m ON m.content_id = c.content_id
LEFT JOIN streamingservice.episode e ON e.content_id = c.content_id
LEFT JOIN streamingservice.series sr ON sr.series_id = e.series_id
LEFT JOIN streamingservice.song s ON s.content_id = c.content_id
ORDER BY c.content_id
""")
SOUND = ("genre", "energy", "valence", "danceability", "instrumental", "sound_group")


def titles(catalog):
    with catalog.connect() as connection:
        return [dict(row._mapping) for row in connection.execute(TITLES) if row.kind is not None]


def sounds(ml):
    with ml.connect() as connection:
        rows = connection.execute(select(songs.c.path, *(songs.c[name] for name in SOUND))
                                  .where(songs.c.problem.is_(None))).all()
        library = Library().refresh(connection)
    return {row.path: {**{name: getattr(row, name) for name in SOUND},
                       "similar": [pick["path"] for pick in library.similar.get(row.path, [])]} for row in rows}


def export(catalog, ml):
    analysed, listed = sounds(ml), []
    for title in titles(catalog):
        if title["kind"] == "song":
            path = song_key(title["uri"] or "")
            if path not in analysed:
                continue
            title["path"] = path
            title["sound"] = analysed[path]
        listed.append({name: value for name, value in title.items() if value is not None})
    return {"exported_at": datetime.now(UTC).isoformat(timespec="seconds"), "titles": listed}


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)
    settings = Settings.from_env()
    if not settings.catalog_url:
        raise SystemExit("ML_CATALOG_DATABASE_URL is not set.")
    exported = export(create_engine(settings.catalog_url), create_engine(settings.database_url))
    Path(args.out).write_text(json.dumps(exported, ensure_ascii=False), encoding="utf-8")
    counts = {}
    for title in exported["titles"]:
        counts[title["kind"]] = counts.get(title["kind"], 0) + 1
    print(f"exported {counts}", flush=True)


if __name__ == "__main__":
    main()
