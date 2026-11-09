import numpy as np
from sklearn.cluster import KMeans
from sklearn.metrics import silhouette_score
from sklearn.neighbors import NearestNeighbors


def cluster_report(X, k_values=range(1, 13), random_state=42):
    report = []
    for k in k_values:
        if k >= len(X):
            break
        model = KMeans(n_clusters=k, n_init=10, random_state=random_state).fit(X)
        silhouette = float(silhouette_score(X, model.labels_)) if k > 1 else None
        report.append({"k": k, "sse": float(model.inertia_), "silhouette": silhouette})
    return report


def best_k(report):
    return max((row for row in report if row["silhouette"] is not None), key=lambda row: row["silhouette"])["k"]


def elbow_k(report):
    points = np.array([[row["k"], row["sse"]] for row in report], dtype=float)
    scaled = (points - points.min(axis=0)) / np.ptp(points, axis=0)
    direction = (scaled[-1] - scaled[0]) / np.linalg.norm(scaled[-1] - scaled[0])
    offsets = scaled - scaled[0]
    distances = np.abs(offsets[:, 0] * direction[1] - offsets[:, 1] * direction[0])
    return int(points[np.argmax(distances), 0])


def most_similar(vectors, count=5):
    neighbours = NearestNeighbors(n_neighbors=min(count + 1, len(vectors)), metric="cosine").fit(vectors)
    distances, indices = neighbours.kneighbors(vectors)
    similar = []
    for item, (row, gaps) in enumerate(zip(indices, distances, strict=True)):
        others = [(int(other), round(1 - float(gap), 4)) for other, gap in zip(row, gaps, strict=True) if other != item]
        similar.append(others[:count])
    return similar
