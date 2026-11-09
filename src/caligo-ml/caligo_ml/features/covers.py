import colorsys

import numpy as np
from PIL import Image
from sklearn.cluster import KMeans


def to_hex(rgb):
    return "#" + "".join(f"{int(round(channel)):02x}" for channel in np.clip(rgb, 0, 255))


def to_rgb(color):
    return tuple(int(color[start:start + 2], 16) / 255 for start in (1, 3, 5))


def palette(image, colors=5, size=64, random_state=42):
    picture = (image if isinstance(image, Image.Image) else Image.open(image)).convert("RGB")
    picture.thumbnail((size, size))
    pixels = np.asarray(picture, dtype=np.float64).reshape(-1, 3)
    clusters = min(colors, len(np.unique(pixels, axis=0)))
    model = KMeans(n_clusters=clusters, n_init=10, random_state=random_state).fit(pixels)
    shares = np.bincount(model.labels_, minlength=clusters) / len(pixels)
    return [(to_hex(model.cluster_centers_[cluster]), round(float(shares[cluster]), 4))
            for cluster in np.argsort(-shares, kind="stable")]


def accent(colors, min_share=0.05, min_chroma=0.15, lightness=0.62):
    best = None
    for color, share in colors:
        hue, light, saturation = colorsys.rgb_to_hls(*to_rgb(color))
        chroma = (1 - abs(2 * light - 1)) * saturation
        if share >= min_share and chroma >= min_chroma:
            score = chroma * np.sqrt(share)
            if best is None or score > best[0]:
                best = (score, hue, saturation)

    if best is None:
        return None

    _, hue, saturation = best
    return to_hex(np.array(colorsys.hls_to_rgb(hue, lightness, min(saturation, 0.75))) * 255)
