import argparse
import json
import re
import tarfile
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

from caligo_ml.datasets import digest, download

MOVIELENS = "https://files.grouplens.org/datasets/movielens/ml-32m.zip"
LASTFM = "http://mtg.upf.edu/static/datasets/last.fm/lastfm-dataset-1K.tar.gz"
ARTICLES = ("the", "a", "an")
QUANTILES = [0.01, 0.05, 0.1, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.75, 0.8, 0.9, 0.95, 0.99]
SESSION_GAP_MINUTES = 30
PLAYS_FILE = "userid-timestamp-artid-artname-traid-traname.tsv"
PROFILES_FILE = "userid-profile.tsv"
TASTE_USERS = 5000
TOP_ARTISTS = (5, 10, 15, 20, 30, 50)
ZONES = {
    "United Kingdom": "Europe/London", "Ireland": "Europe/Dublin", "Portugal": "Europe/Lisbon",
    "Iceland": "Atlantic/Reykjavik", "Germany": "Europe/Berlin", "France": "Europe/Paris", "Spain": "Europe/Madrid",
    "Italy": "Europe/Rome", "Netherlands": "Europe/Amsterdam", "Belgium": "Europe/Brussels",
    "Switzerland": "Europe/Zurich", "Austria": "Europe/Vienna", "Poland": "Europe/Warsaw",
    "Czech Republic": "Europe/Prague", "Slovakia": "Europe/Bratislava", "Hungary": "Europe/Budapest",
    "Slovenia": "Europe/Ljubljana", "Croatia": "Europe/Zagreb", "Serbia": "Europe/Belgrade",
    "Denmark": "Europe/Copenhagen", "Norway": "Europe/Oslo", "Sweden": "Europe/Stockholm",
    "Finland": "Europe/Helsinki", "Estonia": "Europe/Tallinn", "Latvia": "Europe/Riga",
    "Lithuania": "Europe/Vilnius", "Greece": "Europe/Athens", "Romania": "Europe/Bucharest",
    "Bulgaria": "Europe/Sofia", "Ukraine": "Europe/Kyiv", "Belarus": "Europe/Minsk", "Turkey": "Europe/Istanbul",
    "Israel": "Asia/Jerusalem", "India": "Asia/Kolkata", "China": "Asia/Shanghai", "Japan": "Asia/Tokyo",
    "Korea, Republic of": "Asia/Seoul", "Taiwan": "Asia/Taipei", "Philippines": "Asia/Manila",
    "Singapore": "Asia/Singapore", "Thailand": "Asia/Bangkok", "Argentina": "America/Argentina/Buenos_Aires",
    "Chile": "America/Santiago", "Colombia": "America/Bogota", "Peru": "America/Lima",
    "Venezuela": "America/Caracas", "Uruguay": "America/Montevideo", "New Zealand": "Pacific/Auckland",
}


def normalise(title):
    title = title.strip()
    for article in ARTICLES:
        suffix = f", {article.title()}"
        if title.endswith(suffix):
            title = f"{article} {title[:-len(suffix)]}"
    return re.sub(r"[^a-z0-9]+", " ", title.lower()).strip()


def listed_name(listed):
    found = re.search(r"\((\d{4})\)\s*$", listed)
    return listed.split(" (")[0], int(found.group(1)) if found else None


def tokens(title):
    return set(normalise(title).split())


def match_films(movies, wanted):
    catalogue = [(movie_id, listed, *listed_name(listed))
                 for movie_id, listed in zip(movies.movieId, movies.title, strict=True)]
    found = {}
    for film in wanted:
        target, best = tokens(film["title"]), None
        for movie_id, listed, name, year in catalogue:
            if year is None or abs(year - film["year"]) > 1:
                continue
            if normalise(name) == normalise(film["title"]):
                best = (1.0, movie_id, listed)
                break
            overlap = len(target & tokens(name)) / len(target | tokens(name))
            if overlap >= 0.75 and (best is None or overlap > best[0]):
                best = (overlap, movie_id, listed)
        if best is not None:
            found[film["id"]] = best[1:]
    return found


def shares(values, size):
    counts = np.bincount(np.asarray(values, dtype=int), minlength=size)[:size]
    return (counts / max(counts.sum(), 1)).round(5).tolist()


def quantiles(values):
    values = np.asarray(values, dtype=float)
    return {str(q): round(float(np.quantile(values, q)), 4) for q in QUANTILES} if len(values) else {}


def movielens_stats(archive, wanted, seed=42):
    with zipfile.ZipFile(archive) as bundle:
        movies = pd.read_csv(bundle.open("ml-32m/movies.csv"))
        found = match_films(movies, wanted)
        ids = {movie_id: content for content, (movie_id, _) in found.items()}
        ratings = pd.read_csv(bundle.open("ml-32m/ratings.csv"), dtype={"userId": "int32", "movieId": "int32",
                                                                        "rating": "float32", "timestamp": "int64"})
    titles = {film["id"]: film["title"] for film in wanted}
    chosen = ratings[ratings.movieId.isin(ids)].copy()
    chosen["content"] = chosen.movieId.map(ids)
    films = {}
    for content, group in chosen.groupby("content"):
        histogram = group.rating.value_counts(normalize=True).sort_index()
        films[str(content)] = {"title": titles[content], "movielens": found[content][1], "ratings": int(len(group)),
                               "mean": round(float(group.rating.mean()), 3),
                               "histogram": {f"{rating:.1f}": round(float(share), 5)
                                             for rating, share in histogram.items()}}

    per_user = chosen.groupby("userId")
    keen = per_user.size()
    keen = keen[keen >= 3].index
    rng = np.random.default_rng(seed)
    sample = rng.choice(keen, size=min(TASTE_USERS, len(keen)), replace=False)
    tastes = [{str(content): float(rating) for content, rating in zip(group.content, group.rating, strict=True)}
              for _, group in chosen[chosen.userId.isin(sample)].groupby("userId")]

    moments = pd.to_datetime(chosen.timestamp, unit="s", utc=True)
    ordered = chosen.assign(moment=moments).sort_values(["userId", "moment"])
    gaps = ordered.groupby("userId").moment.diff().dropna().dt.total_seconds() / 86400
    return {
        "films": films,
        "matched": {str(content): listed for content, (_, listed) in found.items()},
        "tastes": tastes,
        "viewing": {"hour": shares(moments.dt.hour, 24), "weekday": shares(moments.dt.day_of_week, 7),
                    "days_between_titles": quantiles(gaps[gaps > 0])},
    }


def lastfm_stats(archive, chunk=2_000_000):
    columns = ["user", "timestamp", "artist_id", "artist", "track_id", "track"]
    frames = []
    with tarfile.open(archive) as bundle:
        members = {Path(item.name).name: item for item in bundle.getmembers()}
        profiles = pd.read_csv(bundle.extractfile(members[PROFILES_FILE]), sep="\t", quoting=3)
        reader = pd.read_csv(bundle.extractfile(members[PLAYS_FILE]), sep="\t", names=columns,
                             usecols=["user", "timestamp", "artist", "track"], quoting=3, on_bad_lines="skip",
                             chunksize=chunk)
        for part in reader:
            moments = pd.to_datetime(part.timestamp, utc=True, errors="coerce", format="ISO8601")
            frames.append(part.assign(moment=moments).dropna(subset=["moment"])[["user", "moment", "artist", "track"]])
    plays = pd.concat(frames, ignore_index=True)
    plays[["user", "artist", "track"]] = plays[["user", "artist", "track"]].fillna("").astype("category")
    plays = plays.sort_values(["user", "moment"], ignore_index=True)
    profiles.columns = ["user", "gender", "age", "country", "registered"]

    gap = plays.groupby("user", observed=True).moment.diff().dt.total_seconds().div(60)
    starts = (gap.isna() | (gap > SESSION_GAP_MINUTES)).to_numpy()
    plays["session"] = starts.cumsum()
    session_tracks = plays.groupby("session").size()
    same_artist = (plays.artist == plays.artist.shift()).to_numpy()[~starts]
    session_starts = plays.loc[starts, ["user", "moment"]]
    start_gaps = session_starts.groupby("user", observed=True).moment.diff().dt.total_seconds().div(3600).dropna()

    days = plays.moment.dt.floor("D")
    daily = plays.groupby([plays.user, days], observed=True).size()
    active_days = daily.groupby(level=0, observed=True).size()
    span = days.groupby(plays.user, observed=True).agg(["min", "max"])
    span_days = (span["max"] - span["min"]).dt.days + 1
    repeats = plays.duplicated(["user", "artist", "track"]).groupby(plays.user, observed=True).mean()
    per_artist = plays.groupby(["user", "artist"], observed=True).size()
    loyalty = per_artist.groupby(level=0, observed=True).max() / per_artist.groupby(level=0, observed=True).sum()
    ranked = per_artist.sort_values(ascending=False).groupby(level=0, observed=True)
    within = {}
    for count in TOP_ARTISTS:
        head = ranked.head(count).groupby(level=0, observed=True)
        within[str(count)] = quantiles(head.max() / head.sum())

    zones = profiles.set_index("user").country.map(ZONES).dropna()
    zoned = plays.user.astype(str).map(zones)
    hours, weekdays = np.zeros(24), np.zeros(7)
    for zone in zoned.dropna().unique():
        local = plays.moment[zoned.eq(zone).to_numpy()].dt.tz_convert(zone)
        hours += np.bincount(local.dt.hour, minlength=24)[:24]
        weekdays += np.bincount(local.dt.day_of_week, minlength=7)[:7]
    if hours.sum() == 0:
        hours += np.bincount(plays.moment.dt.hour, minlength=24)[:24]
        weekdays += np.bincount(plays.moment.dt.day_of_week, minlength=7)[:7]
    ages = pd.to_numeric(profiles.age, errors="coerce")
    genders = profiles.gender.dropna().str.lower().value_counts(normalize=True)
    return {
        "listening": {
            "hour": (hours / hours.sum()).round(5).tolist(),
            "weekday": (weekdays / weekdays.sum()).round(5).tolist(),
            "local_time_users": int(zones.index.isin(plays.user.cat.categories).sum()),
            "session_tracks": quantiles(session_tracks),
            "session_gap_hours": quantiles(start_gaps),
            "same_artist_next": round(float(same_artist.mean()), 4) if len(same_artist) else 0.0,
            "daily_plays": quantiles(daily),
            "active_day_share": quantiles(active_days / span_days.reindex(active_days.index)),
            "repeat_share": quantiles(repeats),
            "top_artist_share": quantiles(loyalty),
            "top_artist_share_within": within,
            "users": int(plays.user.nunique()),
            "plays": int(len(plays)),
        },
        "people": {
            "gender": {str(key): round(float(share), 4) for key, share in genders.items()},
            "age": quantiles(ages[(ages >= 13) & (ages <= 90)]),
        },
    }


def youtube_stats(folder):
    wanted = ["video_id", "category_id", "views", "likes", "comment_count", "comments_disabled", "ratings_disabled"]
    frames = [pd.read_csv(path, usecols=wanted, encoding_errors="replace", on_bad_lines="skip")
              .assign(country=path.name[:2]) for path in sorted(Path(folder).rglob("*videos.csv"))]
    videos = pd.concat(frames, ignore_index=True)
    blocked = (videos.comments_disabled.astype(str).str.lower().eq("true")
               | videos.ratings_disabled.astype(str).str.lower().eq("true"))
    for name in ("views", "likes", "comment_count", "category_id"):
        videos[name] = pd.to_numeric(videos[name], errors="coerce")
    videos = videos[~blocked & (videos.views > 0)].dropna(subset=["views", "likes", "comment_count"])
    latest = videos.sort_values("views").groupby(["country", "video_id"]).tail(1)
    latest = latest.assign(like=latest.likes / latest.views, comment=latest.comment_count / latest.views)

    def summary(frame):
        return {"videos": int(len(frame)), "like_per_view": quantiles(frame.like),
                "comment_per_view": quantiles(frame.comment)}

    return {"engagement": {"all": summary(latest), "music": summary(latest[latest.category_id == 10]),
                           "film": summary(latest[latest.category_id == 1])}}


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--cache", default="cache")
    parser.add_argument("--films", required=True, help="JSON list of {id, title, year}")
    parser.add_argument("--youtube", help="folder with the YouTube trending CSV files")
    args = parser.parse_args(argv)

    wanted = json.loads(args.films)
    movielens = download(MOVIELENS, args.cache)
    stats = movielens_stats(movielens, wanted)
    print(f"matched {len(stats['matched'])} of {len(wanted)} films, {len(stats['tastes'])} taste patterns", flush=True)
    sources = {"movielens": {"url": MOVIELENS, "sha256": digest(movielens)}}
    try:
        lastfm = download(LASTFM, args.cache)
        stats.update(lastfm_stats(lastfm))
        sources["lastfm"] = {"url": LASTFM, "sha256": digest(lastfm)}
        print(f"listening from {stats['listening']['plays']} Last.fm plays", flush=True)
    except Exception as error:
        print(f"Last.fm-1K unavailable: {type(error).__name__}: {error}", flush=True)
    if args.youtube and any(Path(args.youtube).rglob("*videos.csv")):
        stats.update(youtube_stats(args.youtube))
        sources["youtube"] = {"dataset": "datasnaek/youtube-new"}
        print(f"engagement from {stats['engagement']['all']['videos']} trending videos", flush=True)
    stats["sources"] = sources
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "demo_stats.json").write_text(json.dumps(stats), encoding="utf-8")


if __name__ == "__main__":
    main()
