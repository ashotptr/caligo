import json
from datetime import datetime, timedelta

import numpy as np
import pytest

from caligo_ml.demo.generate import comment, generate

END = datetime(2026, 11, 7, 12)
FLAT = {"0.01": 1.0, "0.5": 4.0, "0.99": 8.0}


def song(content, artist, album, track, energy, instrumental, genre="Rock"):
    path = f"mixtape/{album}/{content}.mp3"
    return {"content_id": content, "kind": "song", "title": f"Song {content}", "duration": 3.0, "artist": artist,
            "album": album, "track": track, "uri": "/" + path, "path": path,
            "sound": {"genre": genre, "energy": energy, "valence": 0.5, "instrumental": instrumental,
                      "danceability": 0.5, "similar": []}}


@pytest.fixture(scope="module")
def catalog():
    films = [{"content_id": 1, "kind": "film", "title": "Inception", "year": 2010, "duration": 148.0},
             {"content_id": 2, "kind": "film", "title": "Fight Club", "year": 1999, "duration": 139.0},
             {"content_id": 3, "kind": "film", "title": "Gladiator II", "year": 2024, "duration": 148.0}]
    episodes = [{"content_id": 4 + number, "kind": "episode", "title": f"Episode {number + 1}", "duration": 47.0,
                 "series": "Breaking Bad", "season": 1, "episode": number + 1} for number in range(3)]
    songs = [song(10 + index, artist, album, index % 4 + 1, energy, instrumental, genre)
             for index, (artist, album, energy, instrumental, genre) in enumerate(
                 [("Band", "first-album", 0.8, 0.1, "Rock")] * 4 + [("Composer", "score", 0.3, 0.9, "Instrumental")] * 4
                 + [("Singer", "songs", 0.5, 0.2, "Pop")] * 4)]
    for item in songs:
        item["sound"]["similar"] = [other["path"] for other in songs if other is not item][:3]
    return {"titles": films + episodes + songs}


@pytest.fixture(scope="module")
def stats():
    rng = np.random.default_rng(0)
    tastes = [{"19": float(rng.choice([3.5, 4.0, 4.5, 5.0])), "3": float(rng.choice([3.0, 4.0, 5.0]))}
              for _ in range(40)]
    rates = {"like_per_view": {"0.5": 0.5}, "comment_per_view": {"0.5": 0.25}}
    return {
        "films": {"19": {"title": "Inception", "mean": 4.1, "ratings": 100, "histogram": {"4.0": 0.5, "5.0": 0.5}},
                  "3": {"title": "Fight Club", "mean": 4.2, "ratings": 80, "histogram": {"4.0": 0.4, "5.0": 0.6}}},
        "tastes": tastes,
        "viewing": {"weekday": [1, 1, 1, 1, 1, 2, 2]},
        "listening": {"hour": [1] * 8 + [2] * 16, "weekday": [1] * 7, "session_tracks": FLAT, "daily_plays": FLAT,
                      "active_day_share": {"0.01": 0.5, "0.99": 0.9}, "same_artist_next": 0.5,
                      "top_artist_share": {"0.5": 0.4}, "top_artist_share_within": {"5": {"0.5": 0.5}},
                      "repeat_share": {"0.5": 0.3}},
        "people": {"gender": {"m": 0.5, "f": 0.5}, "age": {"0.01": 18.0, "0.99": 40.0}},
        "engagement": {"music": rates, "film": rates},
    }


def plan_for(catalog, stats, **options):
    return generate(catalog, stats, users=12, days=30, end=END, share=1.0, **options)


def test_the_same_seed_gives_the_same_plan(catalog, stats):
    assert json.dumps(plan_for(catalog, stats)) == json.dumps(plan_for(catalog, stats))


def test_every_action_is_dated_inside_the_users_time_and_refers_to_what_they_saw(catalog, stats):
    plan = plan_for(catalog, stats)
    titles = plan["titles"]
    start, end = datetime.fromisoformat(plan["start"]), datetime.fromisoformat(plan["end"])

    assert start == END - timedelta(days=30) and end == END
    assert sum(len(user["watches"]) for user in plan["users"]) > 0
    for user in plan["users"]:
        joined = datetime.fromisoformat(user["joined"])
        moments = [datetime.fromisoformat(moment) for _, moment, _ in user["watches"]]
        seen = {index for index, _, _ in user["watches"]}
        assert start <= joined < end
        assert moments == sorted(moments) and all(joined < moment < end for moment in moments)
        assert {index for index, _, _, _ in user["ratings"]} <= seen
        assert {index for index, _, _ in user["comments"]} <= seen
        assert all(rating is None or 0 <= rating <= 10 for _, _, rating, _ in user["ratings"])
        assert all(joined < datetime.fromisoformat(moment) < end for _, _, _, moment in user["ratings"])
        for shelf in user["collections"]:
            assert {index for index, _ in shelf["titles"]} <= seen
            assert all(item >= shelf["created"] for _, item in shelf["titles"])
    assert all(0 <= index < len(titles) for user in plan["users"] for index, _, _ in user["watches"])


def test_demo_people_look_ordinary_and_never_join_the_newsletter(catalog, stats):
    plan = plan_for(catalog, stats)
    emails = [user["email"] for user in plan["users"]]

    assert len(set(emails)) == len(emails)
    assert all(email.endswith("@demo.invalid") and len(email) <= 50 for email in emails)
    assert all(user["gender"] in ("Male", "Female") for user in plan["users"])
    assert all((END.date() - datetime.fromisoformat(user["dob"]).date()).days >= 16 * 365 for user in plan["users"])
    assert not any("newsletter" in key or "subscri" in key for user in plan["users"] for key in user)


def test_flagged_comments_are_replaced_and_never_kept(catalog, stats):
    flagged = {"Simple and beautiful.", "One of my favourites."}
    plan = plan_for(catalog, stats, check=lambda texts: [text in flagged for text in texts])
    texts = [text for user in plan["users"] for _, text, _ in user["comments"]]

    assert texts and not flagged & set(texts)
    assert plan["report"]["moderation"]["checked"] >= len(texts)


def test_nothing_survives_a_check_that_flags_everything(catalog, stats):
    plan = plan_for(catalog, stats, check=lambda texts: [True] * len(texts))

    assert plan["report"]["comments"] == 0
    assert plan["report"]["moderation"]["dropped"] > 0


def test_comments_fit_what_the_song_sounds_like(catalog):
    instrumental = next(title for title in catalog["titles"] if title.get("artist") == "Composer")
    rng = np.random.default_rng(1)
    texts = {comment(rng, "song", set(), instrumental, plays=1) for _ in range(300)}

    assert "The vocals here are unreal." not in texts
    assert "Love the energy here." not in texts
    assert "On repeat all week." not in texts
    assert "Composer at their best." in texts


def test_the_report_compares_the_simulation_with_its_sources(catalog, stats):
    report = plan_for(catalog, stats)["report"]

    assert report["top_artist_share_median"]["artists"] == 3
    assert report["top_artist_share_median"]["lastfm"] == 0.5
    assert set(report["session_tracks"]) == {"simulated", "lastfm"}
    assert report["watches"]["song"] > 0


def test_the_plan_keeps_the_shape_the_seeder_reads(catalog, stats):
    plan = plan_for(catalog, stats)
    kinds = {title["kind"]: set(title) for title in plan["titles"]}

    assert {"version", "titles", "users"} <= set(plan) and plan["version"] == 1
    assert kinds["film"] == {"kind", "title", "year"}
    assert kinds["episode"] == {"kind", "series", "season", "episode"}
    assert kinds["song"] == {"kind", "uri"}
    assert all(set(user) == {"user_name", "first_name", "last_name", "dob", "gender", "email", "joined", "watches",
                             "ratings", "comments", "collections"} for user in plan["users"])
    shelves = [shelf for user in plan["users"] for shelf in user["collections"]]
    assert all(set(shelf) == {"name", "created", "titles"} for shelf in shelves)


def test_activity_can_be_planned_ahead_for_the_trickle(catalog, stats):
    plan = generate(catalog, stats, users=12, days=30, end=END, share=1.0, ahead=20)
    end, horizon = datetime.fromisoformat(plan["end"]), datetime.fromisoformat(plan["horizon"])
    later = [datetime.fromisoformat(moment) for user in plan["users"] for _, moment, _ in user["watches"]
             if datetime.fromisoformat(moment) > end]

    assert horizon == end + timedelta(days=20)
    assert all(datetime.fromisoformat(user["joined"]) < end for user in plan["users"])
    assert later and all(moment < horizon for moment in later)
    assert plan["report"]["ahead"]["watches"] == len(later)


def test_planning_nothing_ahead_keeps_the_window(catalog, stats):
    plan = generate(catalog, stats, users=12, days=30, end=END, share=1.0, ahead=0)

    assert plan["horizon"] == plan["end"]
    assert plan["report"]["ahead"]["watches"] == 0


def test_comment_texts_go_to_the_earliest_comments_first(catalog, stats):
    plan = generate(catalog, stats, users=12, days=30, end=END, share=1.0, ahead=200)
    moments = sorted(datetime.fromisoformat(moment) for user in plan["users"] for _, _, moment in user["comments"])
    texts = [text for user in plan["users"] for _, text, _ in user["comments"]]

    assert moments and len(texts) == len(set(texts))
    assert moments[0] < END
