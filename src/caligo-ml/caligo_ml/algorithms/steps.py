import numpy as np


def largest_eigenvalue(design, weights=None, iterations=100):
    weights = np.ones(len(design)) if weights is None else np.ravel(weights)
    vector = np.random.default_rng(0).standard_normal(design.shape[1])
    for _ in range(iterations):
        vector = design.T @ (weights * (design @ vector)) / len(design)
        vector /= np.linalg.norm(vector)
    return float(vector @ (design.T @ (weights * (design @ vector))) / len(design))
