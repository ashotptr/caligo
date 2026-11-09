import numpy as np
import pytest
import soundfile
from PIL import Image

from caligo_ml.features import audio, covers, text
from caligo_ml.service.library import segment, song_key, song_uri


def tone(frequency, seconds, rate=audio.RATE):
    t = np.arange(int(seconds * rate)) / rate
    return (0.5 * np.sin(2 * np.pi * frequency * t)).astype(np.float32)


@pytest.fixture(scope="module")
def a440():
    return audio.features(tone(440, 30))


def test_columns_are_the_fma_columns():
    index = audio.columns()

    assert len(index) == 518
    assert list(index.names) == ["feature", "statistics", "number"]
    assert index.is_monotonic_increasing
    assert ("mfcc", "kurtosis", "20") in index
    assert ("tonnetz", "max", "06") in index
    assert ("rmse", "mean", "01") in index


def test_features_come_in_fma_order(a440):
    assert a440.index.equals(audio.columns())
    assert a440.dtype == np.float32
    assert np.isfinite(a440["mfcc"]).all()


def test_a_tone_lands_on_its_pitch_class(a440):
    assert np.argmax(a440["chroma_cqt", "mean"].to_numpy()) == 9
    assert np.argmax(a440["chroma_stft", "mean"].to_numpy()) == 9
    assert np.argmax(a440["chroma_cens", "mean"].to_numpy()) == 9
    assert a440["zcr", "mean", "01"] == pytest.approx(2 * 440 / audio.RATE, rel=0.05)


def test_spectral_features_keep_the_fma_default_rate(a440):
    assert a440["spectral_centroid", "mean", "01"] == pytest.approx(220, rel=0.1)


def test_the_clip_comes_from_the_middle_of_the_song(tmp_path):
    path = tmp_path / "song.wav"
    soundfile.write(path, np.concatenate([tone(220, 20), tone(440, 30), tone(880, 20)]), audio.RATE)
    clip = audio.load_clip(str(path))
    spectrum = np.abs(np.fft.rfft(clip))

    assert len(clip) == 30 * audio.RATE
    assert np.fft.rfftfreq(len(clip), 1 / audio.RATE)[np.argmax(spectrum)] == pytest.approx(440, abs=1)


def test_short_and_silent_audio_is_named():
    assert audio.problem(tone(440, 2)) == "too short"
    assert audio.problem(np.zeros(10 * audio.RATE, dtype=np.float32)) == "silent"
    assert audio.problem(tone(440, 10)) is None


def test_comments_are_cleaned():
    messy = "Check my channel<br />http://x.co/abc &#39;now&#39;\ufeff"
    assert text.clean_comment(messy) == "check my channel httpurl 'now'"
    assert text.clean_comment("Go to www.example.com!") == "go to httpurl"
    assert text.clean_comment("Visit bit.ly/free  please") == "visit httpurl please"
    assert text.clean_comment("I LOVE this song") == "i love this song"


@pytest.mark.parametrize(("name", "expected"), [
    ('first song.mp3', 'first%20song.mp3'),
    ("why'd-you-only-call-me-when-you're-high.mp3", "why'd-you-only-call-me-when-you're-high.mp3"),
    ('tranquility-base-hotel-&-casino', 'tranquility-base-hotel-&-casino'),
    ('exit-music-(for-a-film).mp3', 'exit-music-(for-a-film).mp3'),
    ('what?#\"<>`{}.mp3', 'what%3F%23%22%3C%3E%60%7B%7D.mp3'),
    ("d\u00e9j\u00e0.mp3", "d%C3%A9j%C3%A0.mp3"),
])
def test_segments_match_the_catalogs_mixtape_paths(name, expected):
    assert segment(name) == expected


def test_song_uris_are_encoded_like_the_catalog_and_read_back():
    assert song_uri("mixtape/demo album/a b#c?.mp3") == "/mixtape/demo%20album/a%20b%23c%3F.mp3"
    assert song_uri("music/caf\u00e9 100%.mp3") == "/music/caf%C3%A9%20100%.mp3"
    assert song_key("/mixtape/demo%20album/a%20b%23c%3F.mp3") == "mixtape/demo album/a b#c?.mp3"
    assert song_key("/music/../secrets.mp3") is None
    assert song_key("/mixtape/album/notes.txt") is None
    assert song_key("/mixtape/too/deep/song.mp3") is None


def two_colour_cover():
    image = Image.new("RGB", (40, 20), (200, 30, 30))
    image.paste((30, 30, 200), (20, 0, 40, 20))
    return image


def test_cover_palette_finds_the_colours_and_their_shares():
    colours = covers.palette(two_colour_cover())

    assert {colour for colour, _ in colours} == {"#c81e1e", "#1e1ec8"}
    assert [share for _, share in colours] == [0.5, 0.5]
    assert covers.accent(colours) is not None


def test_a_grey_cover_has_no_accent():
    image = Image.new("RGB", (32, 32), (90, 90, 90))
    image.paste((200, 200, 200), (0, 0, 16, 32))

    assert covers.accent(covers.palette(image)) is None
