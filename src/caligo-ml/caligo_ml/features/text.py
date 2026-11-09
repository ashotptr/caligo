import html
import re

import numpy as np
from sklearn.feature_extraction.text import CountVectorizer, TfidfVectorizer

URL = re.compile(r"(https?://|www\.)\S+|\b\S+\.(com|net|org|ly|be|io)(/\S*)?\b", re.IGNORECASE)
TAG = re.compile(r"<[^>]+>")
INVISIBLE = re.compile("[\u200b-\u200f\ufeff]")
SPACE = re.compile(r"\s+")


def clean_comment(text):
    text = INVISIBLE.sub("", html.unescape(TAG.sub(" ", str(text))))
    text = URL.sub(" httpurl ", text)
    return SPACE.sub(" ", text).strip().lower()


def tfidf_words(**options):
    return TfidfVectorizer(preprocessor=clean_comment, ngram_range=(1, 2), min_df=2, sublinear_tf=True, **options)


def word_presence(**options):
    return CountVectorizer(preprocessor=clean_comment, binary=True, min_df=2, **options)


def drift_profile(texts):
    cleaned = [clean_comment(text) for text in texts]
    return {
        "characters": np.array([len(text) for text in cleaned], dtype=float),
        "words": np.array([len(text.split()) for text in cleaned], dtype=float),
        "links": np.array([text.count("httpurl") for text in cleaned], dtype=float),
    }
