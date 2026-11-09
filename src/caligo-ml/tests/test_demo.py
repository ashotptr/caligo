import io
import tarfile
import zipfile

import pytest

from caligo_ml.demo.stats import lastfm_stats, movielens_stats, youtube_stats

FILMS = [{"id": 19, "title": "Inception", "year": 2010}, {"id": 9, "title": "The Grand Budapest Hotel", "year": 2014},
         {"id": 3, "title": "Fight Club", "year": 1999}, {"id": 18, "title": "Gladiator II", "year": 2024}]
YOUTUBE_HEADER = ("video_id,trending_date,title,channel_title,category_id,publish_time,tags,views,likes,dislikes,"
                  "comment_count,thumbnail_link,comments_disabled,ratings_disabled,video_error_or_removed,description")


@pytest.fixture
def movielens(tmp_path):
    path = tmp_path / "ml-32m.zip"
    movies = ('movieId,title,genres\n1,Inception (2010),Action\n2,"Grand Budapest Hotel, The (2014)",Comedy\n'
              "3,Fight Club (1999),Drama\n4,Other Film (2001),Drama\n")
    rows = ["userId,movieId,rating,timestamp"]
    for user in range(1, 7):
        for movie, rating in ((1, 4.5), (2, 4.0), (3, 5.0 - user * 0.5)):
            rows.append(f"{user},{movie},{rating},{1_600_000_000 + user * 86_400 + movie * 3_600}")
    rows.append("7,1,2.0,1600000000")
    with zipfile.ZipFile(path, "w") as bundle:
        bundle.writestr("ml-32m/movies.csv", movies)
        bundle.writestr("ml-32m/ratings.csv", "\n".join(rows) + "\n")
    return path


def add(bundle, name, text):
    data = text.encode()
    info = tarfile.TarInfo(f"lastfm-dataset-1K/{name}")
    info.size = len(data)
    bundle.addfile(info, io.BytesIO(data))


@pytest.fixture
def lastfm(tmp_path):
    path = tmp_path / "lastfm.tar.gz"
    lines = []
    for minute, (artist, track) in enumerate([("A", "one"), ("A", "two"), ("A", "one"), ("B", "three")]):
        lines.append(f"user_000001\t2009-05-04T20:{minute:02d}:00Z\tid\t{artist}\tid\t{track}")
    lines.append("user_000001\t2009-05-05T09:00:00Z\tid\tA\tid\tone")
    lines.append("user_000002\t2009-05-04T21:00:00Z\tid\tC\tid\tfour")
    profiles = ["#id\tgender\tage\tcountry\tregistered", "user_000001\tf\t\tUnited Kingdom\tAug 13, 2006",
                "user_000002\tm\t22\tUnited States\tFeb 24, 2006"]
    with tarfile.open(path, "w:gz") as bundle:
        add(bundle, "userid-timestamp-artid-artname-traid-traname.tsv", "\n".join(lines) + "\n")
        add(bundle, "userid-profile.tsv", "\n".join(profiles) + "\n")
    return path


@pytest.fixture
def youtube(tmp_path):
    folder = tmp_path / "youtube"
    folder.mkdir()
    us = [YOUTUBE_HEADER, "a,17.14.11,t,c,10,p,x,1000,40,1,4,l,False,False,False,d",
          "a,17.15.11,t,c,10,p,x,2000,60,2,6,l,False,False,False,d",
          "b,17.14.11,t,c,1,p,x,500,10,0,1,l,False,False,False,d",
          "c,17.14.11,t,c,24,p,x,800,8,0,0,l,True,False,False,d"]
    gb = [YOUTUBE_HEADER, "a,17.14.11,t,c,10,p,x,100,5,0,1,l,False,False,False,d"]
    (folder / "USvideos.csv").write_text("\n".join(us) + "\n", encoding="utf-8")
    (folder / "GBvideos.csv").write_text("\n".join(gb) + "\n", encoding="utf-8")
    return folder


def test_movielens_films_are_matched_and_summarised(movielens):
    stats = movielens_stats(movielens, FILMS)

    assert stats["matched"] == {"19": "Inception (2010)", "9": "Grand Budapest Hotel, The (2014)",
                                "3": "Fight Club (1999)"}
    assert stats["films"]["19"]["ratings"] == 7
    assert stats["films"]["9"]["histogram"] == {"4.0": 1.0}
    assert len(stats["tastes"]) == 6
    assert all(set(taste) == {"19", "9", "3"} for taste in stats["tastes"])
    assert sum(stats["viewing"]["hour"]) == pytest.approx(1, abs=1e-4)


def test_listening_sessions_repeats_and_loyalty(lastfm):
    stats = lastfm_stats(lastfm)
    listening = stats["listening"]

    assert listening["users"] == 2
    assert listening["plays"] == 6
    assert listening["session_tracks"]["0.5"] == 1.0
    assert listening["session_tracks"]["0.95"] == pytest.approx(3.7)
    assert listening["top_artist_share"]["0.95"] == pytest.approx(0.99)
    assert listening["same_artist_next"] == pytest.approx(2 / 3, abs=1e-4)
    assert listening["session_gap_hours"]["0.5"] == 13.0
    assert listening["active_day_share"]["0.5"] == 1.0
    assert listening["top_artist_share_within"]["5"]["0.5"] == pytest.approx(0.9)
    assert stats["people"]["gender"] == {"f": 0.5, "m": 0.5}
    assert stats["people"]["age"]["0.5"] == 22.0


def test_listening_hours_are_read_in_the_listeners_own_time_zone(lastfm):
    listening = lastfm_stats(lastfm)["listening"]

    assert listening["local_time_users"] == 1
    assert listening["hour"][21] == pytest.approx(0.8)
    assert listening["hour"][10] == pytest.approx(0.2)
    assert sum(listening["hour"]) == pytest.approx(1, abs=1e-4)


def test_engagement_uses_each_videos_latest_counts_and_skips_closed_comments(youtube):
    engagement = youtube_stats(youtube)["engagement"]

    assert engagement["all"]["videos"] == 3
    assert engagement["music"]["videos"] == 2
    assert engagement["film"]["like_per_view"]["0.5"] == pytest.approx(0.02)
    assert engagement["music"]["comment_per_view"]["0.01"] == pytest.approx(0.0031)
    assert engagement["music"]["like_per_view"]["0.99"] == pytest.approx(0.0498)
