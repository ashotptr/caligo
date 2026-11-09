import argparse
import json
from collections import Counter, defaultdict
from datetime import UTC, datetime, time, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

import numpy as np

from caligo_ml.algorithms import AlternatingLeastSquares
from caligo_ml.demo.stats import normalise, quantiles

SEED = 7
USERS = 100
DAYS = 90
ZONE = "Asia/Yerevan"
LISTENING_SHARE = 0.05
FILM_SHARE = 0.5
FACTORS = 3
REGULARISATION = 5.0
SERIES_START = 0.3
NEXT_EPISODE = 0.85
ABANDON = 0.08
SIMILAR_JUMP = 0.5
COLLECTION_CHANCE = 0.35
COLLECTION_SIZE = (5, 16)
LIKED_RATING = 9.0
CRITICAL_RATING = 4.0
MAX_SESSIONS = 8
FAVOURITE_PLAYS = 3
MAX_SESSION_TRACKS = 40
SESSION_BREAK = timedelta(minutes=30)
ZIPF = np.linspace(0.0, 3.0, 13)
TABLES = {}
MINUTES = {"film": 110.0, "episode": 47.0, "song": 3.5}

NAMES = {
    "armenian": {
        "Female": ["Anahit", "Lilit", "Ani", "Mariam", "Gayane", "Tatev", "Armine", "Narine", "Hasmik", "Sona",
                   "Nare", "Lusine", "Meri", "Elen", "Astghik"],
        "Male": ["Aram", "Davit", "Tigran", "Narek", "Hayk", "Gor", "Levon", "Arman", "Vahe", "Samvel",
                 "Karen", "Erik", "Hovhannes", "Rafael"],
        "Last": ["Hakobyan", "Sargsyan", "Grigoryan", "Harutyunyan", "Karapetyan", "Avetisyan", "Mkrtchyan",
                 "Vardanyan", "Gevorgyan", "Ghazaryan", "Manukyan", "Martirosyan", "Arakelyan", "Babayan",
                 "Hovhannisyan", "Khachatryan", "Melkonyan", "Simonyan", "Danielyan", "Abrahamyan"],
    },
    "international": {
        "Female": ["Emma", "Sofia", "Mia", "Lena", "Clara", "Julia", "Nina", "Elena", "Maria", "Anna", "Laura",
                   "Chloe", "Zoe", "Hannah", "Alice", "Olivia", "Isabel", "Lucia", "Marta", "Ines", "Eva", "Sara",
                   "Nora", "Ida", "Vera"],
        "Male": ["Lucas", "Leo", "Daniel", "David", "Adam", "Max", "Felix", "Noah", "Liam", "Mateo", "Hugo", "Oscar",
                 "Jonas", "Elias", "Mark", "Alex", "Sam", "Theo", "Victor", "Paul", "Simon", "Nico", "Ruben", "Karl",
                 "Emil"],
        "Last": ["Muller", "Schmidt", "Novak", "Rossi", "Bianchi", "Garcia", "Martinez", "Lopez", "Dubois", "Moreau",
                 "Laurent", "Smith", "Brown", "Taylor", "Wilson", "Clarke", "Walker", "Wright", "Hall", "Young",
                 "King", "Scott", "Green", "Baker", "Adams", "Nelson", "Carter", "Mitchell", "Jensen", "Larsen",
                 "Nielsen", "Berg", "Lindqvist", "Virtanen", "Kovacs", "Horvat", "Silva", "Santos", "Costa",
                 "Pereira", "Romano", "Ricci", "Conti", "Weber", "Wagner", "Becker", "Hoffmann", "Fischer", "Meyer"],
    },
}
LOCAL_NAMES = 0.4
COLLECTION_NAMES = ["Late night", "Favourites", "Sunday morning", "Focus", "Road trip", "Rainy days", "Workout",
                    "Calm", "Evening walk", "Best of the year", "Morning coffee", "Weekend", "Study", "Chill"]
COMMENTS = {
    "song": ["This one never gets old.", "On repeat all week.", "The chorus on this is perfect.",
             "Beautiful production.", "Such a good track to end the day with.", "{artist} at their best.",
             "Underrated song.", "The bass line here is great.", "Perfect for a late walk home.",
             "Gives me chills every time.", "Best song on the album.", "This album keeps getting better.",
             "Love the energy here.", "So calm, so good.", "Instant mood lift.",
             "Can't believe I only found this now.",
             "The last minute of this song is everything.", "My favourite from {artist}.", "Great one for focusing.",
             "Turned the volume all the way up for this.", "Simple and beautiful.", "This takes me back.",
             "Still sounds fresh.", "The vocals here are unreal.", "Such a warm sound.",
             "Every time this comes on I stop what I'm doing.", "Great pick for a rainy day.",
             "The intro alone is worth it.", "Lovely melody.", "This deserves more listens.",
             "Perfect road trip song.", "Hits different at night.", "Gorgeous arrangement.", "Never skip this one.",
             "Such a mood.", "Listened to the whole album after this.", "One of the best on here."],
    "film": ["Rewatched it and it still holds up.", "One of my favourites.", "The ending stayed with me for days.",
             "Great cast, great story.", "The soundtrack is amazing.", "Worth every minute.",
             "Didn't expect to like it this much.", "Perfect film for a Sunday evening.", "Beautifully shot.",
             "{title} never gets old.", "Such a fun watch.",
             "The pacing is just right.", "Loved every scene.", "Great one to watch with friends.",
             "Smart and entertaining.", "Better than I remembered.", "The visuals are stunning.",
             "Strong performances all around.", "A classic for a reason.", "Had me hooked from the first scene.",
             "Funny and surprisingly moving.", "Can't recommend it enough.", "The music fits perfectly.",
             "I'd watch this again tonight."],
    "critical": ["Not really for me.", "Too long for what it is.", "Expected more after all the hype.",
                 "Had its moments but dragged in the middle.", "The ending felt rushed.", "Good cast, weak story.",
                 "Couldn't get into it.", "Fine once, wouldn't rewatch.", "A bit predictable.",
                 "Looks great, but the plot is thin.", "Started well, lost me halfway.", "Not as good as I'd hoped.",
                 "Some good scenes, but too slow overall.", "The first half was better.",
                 "Okay for a quiet evening, nothing more."],
    "episode": ["What an episode.", "Can't stop watching this season.", "The tension in this one!",
                "Best episode so far.", "That ending, wow.", "Great start to the series.",
                "Every episode gets better.", "The acting in this episode is incredible.",
                "Slow start, but it pays off.", "Need the next one right now.", "This show is something else."],
}


def curve(table):
    cached = TABLES.get(id(table))
    if cached is None or cached[0] is not table:
        points = sorted((float(level), float(value)) for level, value in table.items())
        values = np.array([value for _, value in points])
        cached = TABLES[id(table)] = (table, np.array([level for level, _ in points]), values,
                                      np.log(np.maximum(values, 1e-6)))
    return cached[1:]


def sound(title, name):
    value = title.get("sound", {}).get(name)
    return 0.5 if value is None else float(value)


FITS = {
    "The chorus on this is perfect.": lambda title, plays: sound(title, "instrumental") < 0.5,
    "The vocals here are unreal.": lambda title, plays: sound(title, "instrumental") < 0.5,
    "Great one for focusing.": lambda title, plays: sound(title, "instrumental") >= 0.5,
    "Love the energy here.": lambda title, plays: sound(title, "energy") >= 0.6,
    "Turned the volume all the way up for this.": lambda title, plays: sound(title, "energy") >= 0.6,
    "So calm, so good.": lambda title, plays: sound(title, "energy") < 0.4,
    "Instant mood lift.": lambda title, plays: sound(title, "valence") >= 0.55,
    "On repeat all week.": lambda title, plays: plays >= 3,
    "Great start to the series.": lambda title, plays: title.get("episode") == 1,
    "Best episode so far.": lambda title, plays: (title.get("episode") or 0) >= 2,
    "Every episode gets better.": lambda title, plays: (title.get("episode") or 0) >= 2,
    "{artist} at their best.": lambda title, plays: bool(title.get("artist")),
    "My favourite from {artist}.": lambda title, plays: bool(title.get("artist")),
}


def sample(rng, table, size=None, log=False):
    levels, values, logs = curve(table)
    draws = rng.uniform(levels[0], levels[-1], size)
    return np.exp(np.interp(draws, levels, logs)) if log else np.interp(draws, levels, values)


def median(table):
    return float(table["0.5"])


def key(title):
    if title["kind"] == "film":
        return {"kind": "film", "title": title["title"], "year": title.get("year")}
    if title["kind"] == "episode":
        return {"kind": "episode", "series": title["series"], "season": title["season"], "episode": title["episode"]}
    return {"kind": "song", "uri": title["uri"]}


def stamp(moment):
    return moment.isoformat(timespec="seconds")


def at(day, hour, minute, zone):
    return datetime.combine(day, time(int(hour), int(minute)), tzinfo=zone).astimezone(UTC).replace(tzinfo=None)


def local_day(moment, zone):
    return moment.replace(tzinfo=UTC).astimezone(zone).date()


def minutes(title):
    return float(title.get("duration") or MINUTES[title["kind"]])


class Catalog:
    def __init__(self, exported):
        titles = exported["titles"]
        self.films = [title for title in titles if title["kind"] == "film"]
        self.episodes = sorted((title for title in titles if title["kind"] == "episode"),
                               key=lambda title: (title["series"], title["season"], title["episode"]))
        self.songs = [title for title in titles if title["kind"] == "song" and "sound" in title]
        self.by_path = {song["path"]: song for song in self.songs}
        self.by_artist = defaultdict(list)
        albums = defaultdict(list)
        for song in self.songs:
            self.by_artist[self.artist(song)].append(song)
            albums[song.get("album") or self.artist(song)].append(song)
        self.after = {}
        for tracks in albums.values():
            tracks.sort(key=lambda song: (song.get("track") or 0, song["title"]))
            for current, following in zip(tracks, tracks[1:], strict=False):
                self.after[current["path"]] = following
        self.artists = sorted(self.by_artist)
        self.genres = Counter(song["sound"]["genre"] for song in self.songs)

    @staticmethod
    def artist(song):
        return song.get("artist") or song.get("album") or "unknown"


class Titles:
    def __init__(self):
        self.keys, self.index = [], {}

    def __call__(self, title):
        identity = title["content_id"]
        if identity not in self.index:
            self.index[identity] = len(self.keys)
            self.keys.append(key(title))
        return self.index[identity]


class Model:
    def __init__(self, stats, catalog, zone):
        self.stats, self.catalog, self.zone = stats, catalog, zone
        listening = stats["listening"]
        self.hours = np.asarray(listening["hour"], dtype=float) / sum(listening["hour"])
        self.clock = np.cumsum(self.hours)
        self.weekdays = np.asarray(listening["weekday"], dtype=float) * 7 / sum(listening["weekday"])
        self.film_weekdays = np.asarray(stats["viewing"]["weekday"], dtype=float)
        self.film_weekdays /= self.film_weekdays.sum()
        films = {normalise(film["title"]): film for film in stats["films"].values()}
        self.matched = {film["content_id"]: films[normalise(film["title"])] for film in catalog.films
                        if normalise(film["title"]) in films}
        by_title = {content: normalise(film["title"]) for content, film in stats["films"].items()}
        local = {normalise(film["title"]): film for film in catalog.films}
        self.tastes = [{local[by_title[content]]["content_id"]: stars for content, stars in taste.items()
                        if by_title.get(content) in local} for taste in stats["tastes"]]
        self.tastes = [taste for taste in self.tastes if taste]
        self.film_mean = float(np.mean([film["mean"] for film in self.matched.values()])) if self.matched else 3.5
        self.films = sorted(self.matched)
        column = {content: index for index, content in enumerate(self.films)}
        cells = [(user, column[content], stars) for user, taste in enumerate(self.tastes)
                 for content, stars in taste.items()]
        self.factors = None
        if cells:
            users, items, stars = zip(*cells, strict=True)
            self.factors = AlternatingLeastSquares(rank=FACTORS, regularisation=REGULARISATION,
                                                   random_state=SEED).fit(users, items, stars)
            self.bias_spread = float(np.std(self.factors.user_bias_))
            self.taste_spread = np.atleast_2d(np.cov(self.factors.user_factors_, rowvar=False))
        reach = Counter(content for taste in self.tastes for content in taste)
        self.reach = np.array([reach[content] / len(self.tastes) for content in self.films])
        self.unmatched_reach = float(np.median(self.reach)) if len(self.reach) else 0.2
        self.sizes = [len(taste) for taste in self.tastes] or [3]
        engagement = stats["engagement"]
        self.like_song = median(engagement["music"]["like_per_view"])
        self.like_film = median(engagement["film"]["like_per_view"])
        self.comment_after_like = {
            "song": median(engagement["music"]["comment_per_view"]) / max(self.like_song, 1e-9),
            "film": median(engagement["film"]["comment_per_view"]) / max(self.like_film, 1e-9),
        }

    def moment(self, rng, day):
        hour = min(int(np.searchsorted(self.clock, rng.random())), 23)
        return at(day, hour, rng.integers(0, 60), self.zone)


def person(rng, stats, taken, end):
    genders = stats["people"]["gender"]
    female = genders.get("f", 0.5) / (genders.get("f", 0.5) + genders.get("m", 0.5))
    gender = "Female" if rng.random() < female else "Male"
    names = NAMES["armenian" if rng.random() < LOCAL_NAMES else "international"]
    first, last = rng.choice(names[gender]), rng.choice(names["Last"])
    age = int(np.clip(sample(rng, stats["people"]["age"]), 16, 75))
    born = end.date() - timedelta(days=int(age * 365.25) + int(rng.integers(0, 365)))
    handle = f"{first}.{last}".lower()
    if handle in taken or rng.random() < 0.5:
        handle += f"{born.year % 100:02d}"
    while handle in taken:
        handle += str(rng.integers(0, 10))
    taken.add(handle)
    return {"user_name": handle, "first_name": first, "last_name": last, "dob": born.isoformat(), "gender": gender}


def song_taste(rng, catalog):
    genres = list(catalog.genres)
    weights = np.array([catalog.genres[genre] for genre in genres], dtype=float)
    favourite = genres[rng.choice(len(genres), p=weights / weights.sum())]
    affinity = {artist: np.mean([song["sound"]["genre"] == favourite for song in songs]) + rng.normal(0, 0.25)
                for artist, songs in catalog.by_artist.items()}
    return sorted(catalog.artists, key=lambda artist: -affinity[artist])


def pick(rng, catalog, order, weights):
    artist = order[min(int(np.searchsorted(weights, rng.random())), len(order) - 1)]
    songs = catalog.by_artist[artist]
    return songs[rng.integers(0, len(songs))]


def session(rng, catalog, order, weights, length, same_artist):
    current = pick(rng, catalog, order, weights)
    tracks = [current]
    while len(tracks) < length:
        similar = [catalog.by_path[path] for path in current["sound"].get("similar", []) if path in catalog.by_path]
        if rng.random() < same_artist:
            following = catalog.after.get(current["path"])
            if following is None:
                own = catalog.by_artist[catalog.artist(current)]
                following = own[rng.integers(0, len(own))]
            current = following
        elif similar and rng.random() < SIMILAR_JUMP:
            current = similar[rng.integers(0, len(similar))]
        else:
            current = pick(rng, catalog, order, weights)
        tracks.append(current)
    return tracks


def listening(seed, model, joined, end, share, exponent):
    rng = np.random.default_rng(seed)
    catalog, stats = model.catalog, model.stats["listening"]
    order = song_taste(rng, catalog)
    weights = np.cumsum(np.arange(1, len(order) + 1, dtype=float) ** -exponent)
    weights /= weights[-1]
    active = float(sample(rng, stats["active_day_share"]))
    plays, day, last = [], local_day(joined, model.zone), local_day(end, model.zone)
    while day <= last:
        if rng.random() < min(1.0, active * model.weekdays[day.weekday()]):
            count = int(np.clip(round(float(sample(rng, stats["daily_plays"], log=True))
                                      / float(sample(rng, stats["session_tracks"], log=True))), 1, MAX_SESSIONS))
            cursor = None
            for start in sorted(model.moment(rng, day) for _ in range(count)):
                if rng.random() >= share:
                    continue
                length = int(np.clip(round(float(sample(rng, stats["session_tracks"], log=True))), 1,
                                     MAX_SESSION_TRACKS))
                tracks = session(rng, catalog, order, weights, length, stats["same_artist_next"])
                if cursor is not None and start < cursor + SESSION_BREAK:
                    start = cursor + SESSION_BREAK + timedelta(minutes=int(rng.integers(0, 90)))
                for song in tracks:
                    if start >= end:
                        break
                    if start > joined:
                        plays.append((start, song))
                    start += timedelta(minutes=minutes(song))
                cursor = start
        day += timedelta(days=1)
    return plays


def half_star(stars):
    return float(np.clip(np.round(stars * 2) / 2, 0.5, 5.0))


def film_tastes(rng, model):
    factors, chosen = model.factors, {}
    if factors is None:
        return chosen, model.film_mean, 0.8
    bias = rng.normal(0, model.bias_spread)
    taste = rng.multivariate_normal(np.zeros(factors.rank), model.taste_spread)
    predicted = factors.predict_for(bias, taste, np.arange(len(model.films)))
    weights = model.reach * np.exp(predicted - predicted.max())
    size = min(model.sizes[rng.integers(0, len(model.sizes))], len(model.films))
    for index in rng.choice(len(model.films), size=size, replace=False, p=weights / weights.sum()):
        if rng.random() < FILM_SHARE:
            chosen[model.films[index]] = half_star(predicted[index] + rng.normal(0, factors.residual_std_))
    return chosen, factors.mean_ + bias, factors.residual_std_


def film_watches(rng, model, joined, end, horizon=None):
    chosen, unknown, spread = film_tastes(rng, model)
    for film in model.catalog.films:
        if film["content_id"] not in model.matched and rng.random() < model.unmatched_reach * FILM_SHARE:
            chosen[film["content_id"]] = half_star(unknown + rng.normal(0, spread))
    chosen = dict(sorted(chosen.items(), key=lambda _: rng.random()))
    stop = min(horizon or end, joined + timedelta(days=DAYS))
    first, last = local_day(joined, model.zone), local_day(stop, model.zone)
    days = [first + timedelta(days=offset) for offset in range((last - first).days + 1)]
    weights = np.array([model.film_weekdays[day.weekday()] for day in days])
    picked = rng.choice(len(days), size=min(len(chosen), len(days)), replace=False, p=weights / weights.sum())
    films = {film["content_id"]: film for film in model.catalog.films}
    watches = []
    for (content, stars), index in zip(chosen.items(), sorted(picked), strict=False):
        moment = model.moment(rng, days[index])
        if not joined < moment < stop:
            continue
        film = films[content]
        if rng.random() < ABANDON:
            watches.append((moment, film, float(round(rng.uniform(0.1, 0.9) * minutes(film) * 60)), None))
        else:
            watches.append((moment, film, None, stars * 2))
    return watches


def episode_watches(rng, model, joined, end, horizon=None):
    episodes = model.catalog.episodes
    if not episodes or rng.random() >= SERIES_START:
        return []
    horizon = horizon or end
    first, last = local_day(joined, model.zone), local_day(horizon, model.zone)
    day = first + timedelta(days=int(rng.integers(0, max(1, (local_day(end, model.zone) - first).days // 2 + 1))))
    watches, position = [], 0
    while position < len(episodes) and day <= last:
        moment = model.moment(rng, day)
        for _ in range(min(1 + rng.poisson(0.7), 3)):
            if position >= len(episodes) or (position and rng.random() >= NEXT_EPISODE):
                return [watch for watch in watches if joined < watch[0] < horizon]
            episode = episodes[position]
            if rng.random() < ABANDON:
                watches.append((moment, episode, float(round(rng.uniform(0.1, 0.9) * minutes(episode) * 60)), None))
                return [watch for watch in watches if joined < watch[0] < horizon]
            watches.append((moment, episode, None, None))
            moment += timedelta(minutes=minutes(episode) + int(rng.integers(2, 15)))
            position += 1
        day += timedelta(days=1 + int(rng.geometric(0.4)))
    return [watch for watch in watches if joined < watch[0] < horizon]


def comment(rng, bank, used, title, plays=1):
    choices = [text for text in COMMENTS[bank] if (bank, text) not in used
               and FITS.get(text, lambda title, plays: True)(title, plays)]
    if not choices:
        return None
    text = choices[rng.integers(0, len(choices))]
    used.add((bank, text))
    return text.format(artist=title.get("artist", ""), title=title.get("title", ""))


def engagement(rng, model, plays, films, episodes):
    reactions, wanted = {}, []
    per_song = defaultdict(list)
    for moment, song in plays:
        per_song[song["content_id"]].append((moment, song))
    heard = {content: len(times) for content, times in per_song.items()}
    for times in per_song.values():
        if rng.random() < 1 - (1 - model.like_song) ** len(times):
            moment, song = times[rng.integers(0, len(times))]
            reactions[song["content_id"]] = (moment, song, True, None)
    for moment, film, _, rating in films:
        if rating is not None:
            reactions[film["content_id"]] = (moment, film, True if rating >= LIKED_RATING else None, rating)
    for moment, episode, _, _ in episodes:
        if rng.random() < model.like_film:
            reactions[episode["content_id"]] = (moment, episode, True, None)
    for moment, title, liked, rating in sorted(reactions.values(), key=lambda reaction: reaction[0]):
        ratio = model.comment_after_like["song" if title["kind"] == "song" else "film"]
        if liked and rng.random() < ratio:
            bank = title["kind"]
        elif rating is not None and rating <= CRITICAL_RATING and rng.random() < ratio:
            bank = "critical"
        else:
            continue
        wanted.append((moment + timedelta(minutes=int(rng.integers(1, 20))), title, bank,
                       heard.get(title["content_id"], 1)))
    return reactions, wanted


def collection(rng, reactions, plays):
    counts, qualified = Counter(), {}
    for moment, title, liked, _ in sorted(reactions.values(), key=lambda reaction: reaction[0]):
        if liked:
            qualified.setdefault(title["content_id"], (moment, title))
    for moment, song in plays:
        counts[song["content_id"]] += 1
        if counts[song["content_id"]] == FAVOURITE_PLAYS:
            qualified.setdefault(song["content_id"], (moment, song))
    ready = sorted(qualified.values(), key=lambda item: item[0])
    if len(ready) < COLLECTION_SIZE[0] or rng.random() >= COLLECTION_CHANCE:
        return None
    created = ready[COLLECTION_SIZE[0] - 1][0] + timedelta(minutes=int(rng.integers(1, 30)))
    chosen = ready[:int(rng.integers(*COLLECTION_SIZE))]
    added = [(max(moment, created) + timedelta(minutes=int(rng.integers(0, 5))), title) for moment, title in chosen]
    return {"name": COLLECTION_NAMES[rng.integers(0, len(COLLECTION_NAMES))], "created": created,
            "titles": sorted(added, key=lambda item: item[0])}


def write_comments(wanted, check, rng, used):
    report = {"checked": 0, "replaced": 0, "dropped": 0}
    written = []
    for index, (moment, title, bank, plays) in sorted(wanted, key=lambda item: item[1][0]):
        text = comment(rng, bank, used, title, plays)
        while text is not None and check is not None:
            report["checked"] += 1
            if not check([text])[0]:
                break
            report["replaced"] += 1
            text = comment(rng, bank, used, title, plays)
        if text is None:
            report["dropped"] += 1
            continue
        written.append((index, moment, title, text))
    return written, report


def artist_shares(plays_by_user):
    tops, repeats = [], []
    for plays in plays_by_user:
        if len(plays) < 10:
            continue
        artists = Counter(Catalog.artist(song) for _, song in plays)
        tops.append(max(artists.values()) / len(plays))
        repeats.append(1 - len({song["content_id"] for _, song in plays}) / len(plays))
    return tops, repeats


def film_report(model, by_film):
    rated = [content for content in model.films if len(by_film[content]) >= 3]
    report = {"rank": FACTORS, "matched": len(model.films), "rated_three_times": len(rated)}
    if model.factors is not None:
        report["training_rmse"] = round(model.factors.residual_std_, 4)
    if len(rated) >= 3:
        simulated = [np.mean(by_film[content]) for content in rated]
        real = [model.matched[content]["mean"] for content in rated]
        report["mean_rating_correlation"] = round(float(np.corrcoef(simulated, real)[0, 1]), 4)
        pooled, source = Counter(), Counter()
        for content in rated:
            pooled.update(f"{stars:.1f}" for stars in by_film[content])
            for stars, share in model.matched[content]["histogram"].items():
                source[stars] += share * model.matched[content]["ratings"]
        total, weight = sum(pooled.values()), sum(source.values())
        report["histogram_distance"] = round(0.5 * sum(abs(pooled[stars] / total - source[stars] / weight)
                                                       for stars in set(pooled) | set(source)), 4)
    return report


def generate(exported, stats, users=USERS, days=DAYS, end=None, zone=ZONE, seed=SEED, share=LISTENING_SHARE,
             check=None, ahead=0):
    zone = ZoneInfo(zone)
    end = end or datetime.now(UTC).replace(tzinfo=None, minute=0, second=0, microsecond=0)
    start = end - timedelta(days=days)
    horizon = end + timedelta(days=ahead)
    catalog = Catalog(exported)
    model = Model(stats, catalog, zone)
    taken, people = set(), []
    for index in range(users):
        rng = np.random.default_rng([seed, index, 0])
        profile = person(rng, stats, taken, end)
        offset = timedelta(minutes=int(rng.integers(0, int((days - 7) * 24 * 60))))
        people.append((profile, start + offset))

    within = stats["listening"].get("top_artist_share_within", {})
    nearest = min(within, key=lambda count: abs(int(count) - len(catalog.artists))) if within else None
    target = median(within[nearest] if nearest else stats["listening"]["top_artist_share"])
    trials = {}
    for exponent in ZIPF:
        heard = [listening([seed, index, 1], model, joined, horizon, share, exponent)
                 for index, (_, joined) in enumerate(people)]
        tops, _ = artist_shares(heard)
        trials[float(exponent)] = (abs(float(np.median(tops)) - target) if tops else float("inf"), heard)
    exponent = min(trials, key=lambda value: trials[value][0])
    heard = trials[exponent][1]

    titles, plan_users, used, wanted = Titles(), [], set(), []
    totals, every_session, hours = Counter(), [], np.zeros(24)
    ratings, liked_count, by_film = [], 0, defaultdict(list)
    for index, (profile, joined) in enumerate(people):
        plays = heard[index]
        films = film_watches(np.random.default_rng([seed, index, 2]), model, joined, end, horizon)
        episodes = episode_watches(np.random.default_rng([seed, index, 3]), model, joined, end, horizon)
        rng = np.random.default_rng([seed, index, 4])
        reactions, asked = engagement(rng, model, plays, films, episodes)
        wanted += [(index, item) for item in asked]
        shelf = collection(rng, reactions, plays)

        watches = sorted([(moment, song, None) for moment, song in plays]
                         + [(moment, title, position) for moment, title, position, _ in films + episodes],
                         key=lambda watch: watch[0])
        for moment, title, _ in watches:
            totals[title["kind"]] += 1
            if title["kind"] == "song":
                hours[moment.replace(tzinfo=UTC).astimezone(zone).hour] += 1
        sessions, previous = [], None
        for moment, _ in plays:
            if previous is None or moment - previous > SESSION_BREAK:
                sessions.append(0)
            sessions[-1] += 1
            previous = moment
        every_session += sessions
        ratings += [reaction[3] for reaction in reactions.values() if reaction[3] is not None]
        for _, title, _, rating in reactions.values():
            if rating is not None and title["content_id"] in model.matched:
                by_film[title["content_id"]].append(rating / 2)
        liked_count += sum(1 for reaction in reactions.values() if reaction[2])
        entry = {**profile, "email": f"{profile['user_name']}@demo.invalid", "joined": stamp(joined),
                 "watches": [[titles(title), stamp(moment), position] for moment, title, position in watches],
                 "ratings": [[titles(title), liked, rating, stamp(moment)] for moment, title, liked, rating
                             in sorted(reactions.values(), key=lambda reaction: reaction[0])],
                 "comments": [],
                 "collections": []}
        if shelf is not None:
            entry["collections"].append({"name": shelf["name"], "created": stamp(shelf["created"]),
                                         "titles": [[titles(song), stamp(moment)] for moment, song in shelf["titles"]]})
        plan_users.append(entry)

    written, moderation = write_comments(wanted, check, np.random.default_rng([seed, 5]), used)
    for index, moment, title, text in written:
        plan_users[index]["comments"].append([titles(title), text, stamp(moment)])

    tops, repeats = artist_shares(heard)
    source = stats["listening"]
    report = {
        "users": users,
        "watches": dict(totals),
        "ratings": len(ratings),
        "likes": liked_count,
        "comments": sum(len(user["comments"]) for user in plan_users),
        "collections": sum(len(user["collections"]) for user in plan_users),
        "zipf_exponent": exponent,
        "session_tracks": {"simulated": quantiles(every_session), "lastfm": source["session_tracks"]},
        "top_artist_share_median": {"simulated": round(float(np.median(tops)), 4) if tops else None,
                                    "lastfm": target, "artists": len(catalog.artists),
                                    "lastfm_top_artists": int(nearest) if nearest else None},
        "repeat_share_median": {"simulated": round(float(np.median(repeats)), 4) if repeats else None,
                                "lastfm": source["repeat_share"]["0.5"]},
        "hour_correlation": (round(float(np.corrcoef(hours, model.hours)[0, 1]), 4) if hours.sum() else None),
        "rating_mean": {"simulated": round(float(np.mean(ratings)) / 2, 3) if ratings else None,
                        "movielens": round(model.film_mean, 3)},
        "films": film_report(model, by_film),
        "moderation": moderation,
    }
    plan_users.sort(key=lambda user: user["joined"])
    later = stamp(end)
    report["ahead"] = {
        "days": ahead,
        "watches": sum(1 for user in plan_users for item in user["watches"] if item[1] > later),
        "ratings": sum(1 for user in plan_users for item in user["ratings"] if item[3] > later),
        "comments": sum(1 for user in plan_users for item in user["comments"] if item[2] > later),
    }
    return {"version": 1, "seed": seed, "zone": str(zone), "start": stamp(start), "end": stamp(end),
            "horizon": stamp(horizon), "titles": titles.keys, "users": plan_users, "report": report}


def moderation_check(models):
    from caligo_ml.service import scoring
    from caligo_ml.service.champions import Champions

    champions = Champions(models)
    champions.refresh()
    return lambda texts: [result["flagged"] for result in scoring.comments(champions, texts)]


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--stats", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--models", help="ML root with the moderation champions")
    parser.add_argument("--users", type=int, default=USERS)
    parser.add_argument("--days", type=int, default=DAYS)
    parser.add_argument("--end", help="UTC end of the window, ISO format")
    parser.add_argument("--zone", default=ZONE)
    parser.add_argument("--seed", type=int, default=SEED)
    parser.add_argument("--listening-share", type=float, default=LISTENING_SHARE)
    parser.add_argument("--ahead-days", type=int, default=0, help="days planned past --end, for the trickle")
    args = parser.parse_args(argv)
    exported = json.loads(Path(args.catalog).read_text(encoding="utf-8"))
    stats = json.loads(Path(args.stats).read_text(encoding="utf-8"))
    end = datetime.fromisoformat(args.end).replace(tzinfo=None) if args.end else None
    check = moderation_check(args.models) if args.models else None
    plan = generate(exported, stats, args.users, args.days, end, args.zone, args.seed, args.listening_share, check,
                    args.ahead_days)
    Path(args.out).write_text(json.dumps(plan, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(plan["report"], indent=2), flush=True)


if __name__ == "__main__":
    main()
