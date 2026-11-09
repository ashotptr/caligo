from urllib.parse import unquote

import numpy as np
from sklearn.cluster import KMeans
from sklearn.decomposition import PCA
from sklearn.preprocessing import StandardScaler
from sqlalchemy import select

from caligo_ml.clustering import best_k, cluster_report, most_similar
from caligo_ml.service.store import songs

BROWSER_ENCODED = set(b'"#<>?`{}')


def segment(name):
    return "".join(f"%{octet:02X}" if octet <= 0x20 or octet >= 0x7F or octet in BROWSER_ENCODED else chr(octet)
                   for octet in name.encode("utf-8"))


def song_uri(path):
    return "/" + "/".join(segment(part) for part in path.split("/"))


def song_key(uri):
    path = unquote(uri).replace("\\", "/").lstrip("/")
    parts = path.split("/")
    shaped = (len(parts) == 3 and parts[0] == "mixtape") or (len(parts) == 2 and parts[0] == "music")
    if not shaped or any(part in ("", ".", "..") for part in parts) or not path.lower().endswith(".mp3"):
        return None
    return path


class Library:
    def __init__(self):
        self.paths = []
        self.vectors = None
        self.similar = {}
        self.source = "features"

    def refresh(self, connection):
        rows = connection.execute(
            select(songs.c.path, songs.c.title, songs.c.artist, songs.c.features, songs.c.embedding)
            .where(songs.c.problem.is_(None), songs.c.features.is_not(None))
            .order_by(songs.c.path)).all()
        self.paths = [row.path for row in rows]
        if len(rows) < 2:
            self.vectors, self.similar = None, {}
            return self
        heard = all(row.embedding is not None for row in rows)
        self.source = "embedding" if heard else "features"
        vectors = np.array([row.embedding if heard else row.features for row in rows], dtype=float)
        self.vectors = StandardScaler().fit_transform(vectors)
        names = [((row.artist or "").casefold(), (row.title or "").casefold()) for row in rows]
        self.similar = {}
        for item, neighbours in enumerate(most_similar(self.vectors, count=15)):
            seen, picks = {names[item]}, []
            for other, score in neighbours:
                if names[other] in seen:
                    continue
                seen.add(names[other])
                picks.append({"path": rows[other].path, "uri": song_uri(rows[other].path),
                              "title": rows[other].title, "artist": rows[other].artist, "score": score})
                if len(picks) == 5:
                    break
            self.similar[self.paths[item]] = picks
        return self

    def groups(self):
        if self.vectors is None or len(self.paths) < 4:
            return {}, []
        reduced = PCA(n_components=min(20, len(self.paths) - 1), random_state=42).fit_transform(self.vectors)
        report = cluster_report(reduced, range(1, min(13, len(self.paths))))
        labels = KMeans(n_clusters=best_k(report), n_init=10, random_state=42).fit_predict(reduced)
        return dict(zip(self.paths, labels.tolist(), strict=True)), report
