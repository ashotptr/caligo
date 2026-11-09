import argparse
import io
import json
import platform
import time
import zipfile
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import joblib
import librosa
import numpy as np
import pandas as pd
import torch
import torchvision
from torch import nn

from caligo_ml.datasets import FMA_METADATA, GENRES, digest, download
from caligo_ml.evaluation import classification_metrics, frozen_split, reference_bins
from caligo_ml.features.audio import MEL_RATE, fit_frames, log_mel, spectrogram_profile

FMA_SMALL = "https://os.unil.cloud.switch.ch/fma/fma_small.zip"
CROP = 431
SEED = 42


def small_genres(metadata):
    with zipfile.ZipFile(metadata) as bundle:
        tracks = pd.read_csv(bundle.open("fma_metadata/tracks.csv"), index_col=0, header=[0, 1], low_memory=False)
    small = tracks[tracks["set", "subset"] == "small"]
    return small["track", "genre_top"]


def spectrogram(job):
    archive, member = job
    try:
        with zipfile.ZipFile(archive) as bundle:
            samples, _ = librosa.load(io.BytesIO(bundle.read(member)), sr=MEL_RATE, mono=True)
    except Exception:
        return None
    if len(samples) < MEL_RATE * 20:
        return None
    return fit_frames(log_mel(samples, MEL_RATE)).astype(np.float16)


def spectrograms(archive, genres, limit=None, workers=4):
    with zipfile.ZipFile(archive) as bundle:
        members = {int(Path(name).stem): name for name in bundle.namelist() if name.endswith(".mp3")}
    ids = [track for track in sorted(members) if track in genres.index][:limit]
    with ProcessPoolExecutor(workers) as pool:
        mels = list(pool.map(spectrogram, [(archive, members[track]) for track in ids], chunksize=16))
    kept = [index for index, mel in enumerate(mels) if mel is not None]
    return (np.stack([mels[index] for index in kept]), genres.loc[[ids[index] for index in kept]].to_numpy(dtype=str),
            np.array([ids[index] for index in kept]))


class Network(nn.Module):
    def __init__(self, classes, pretrained=True):
        super().__init__()
        weights = torchvision.models.ResNet18_Weights.IMAGENET1K_V1 if pretrained else None
        backbone = torchvision.models.resnet18(weights=weights)
        merged = backbone.conv1.weight.sum(dim=1, keepdim=True)
        backbone.conv1 = nn.Conv2d(1, 64, kernel_size=7, stride=2, padding=3, bias=False)
        backbone.conv1.weight = nn.Parameter(merged)
        backbone.fc = nn.Identity()
        self.backbone = backbone
        self.head = nn.Sequential(nn.Dropout(0.3), nn.Linear(512, classes))

    def forward(self, mel):
        embedding = self.backbone(mel)
        return self.head(embedding), embedding


class Exported(nn.Module):
    def __init__(self, network):
        super().__init__()
        self.network = network

    def forward(self, mel):
        logits, embedding = self.network(mel)
        return torch.softmax(logits, dim=1), embedding


def masked(batch, rng):
    for item in batch:
        band = rng.integers(0, 16)
        low = rng.integers(0, item.shape[1] - band + 1)
        item[:, low:low + band, :] = 0
        width = rng.integers(0, 40)
        start = rng.integers(0, item.shape[2] - width + 1)
        item[:, :, start:start + width] = 0
    return batch


def windows(mels, rows, mean, std, starts):
    return np.stack([(mels[row, :, start:start + CROP].astype(np.float32) - mean) / std
                     for row in rows for start in starts])[:, None]


def scores(network, mels, rows, mean, std, device, size=32):
    starts = np.linspace(0, mels.shape[2] - CROP, 3).astype(int)
    network.eval()
    chances = []
    with torch.no_grad():
        for begin in range(0, len(rows), size):
            chosen = rows[begin:begin + size]
            batch = torch.from_numpy(windows(mels, chosen, mean, std, starts)).to(device)
            logits, _ = network(batch)
            chances.append(torch.softmax(logits.float(), dim=1).reshape(len(chosen), len(starts), -1).mean(1).cpu())
    return torch.cat(chances).numpy()


def fit(mels, labels, ids, classes, device, epochs=30, patience=None, size=64, pretrained=True):
    rng = np.random.default_rng(SEED)
    torch.manual_seed(SEED)
    split = frozen_split(ids)
    train, validation, test = (np.flatnonzero(split == part) for part in ("train", "validation", "test"))
    targets = np.searchsorted(classes, labels)
    sample = mels[train[:2000]].astype(np.float32)
    mean, std = float(sample.mean()), float(sample.std())

    network = Network(len(classes), pretrained).to(device)
    optimiser = torch.optim.AdamW(network.parameters(), lr=1e-3, weight_decay=1e-4)
    steps = max(1, int(np.ceil(len(train) / size)))
    schedule = torch.optim.lr_scheduler.OneCycleLR(optimiser, max_lr=1e-3, epochs=epochs, steps_per_epoch=steps)
    scaler = torch.amp.GradScaler(enabled=device.type == "cuda")
    best, best_state, waited, history = -1.0, None, 0, []
    started = time.perf_counter()

    for epoch in range(epochs):
        network.train()
        order = rng.permutation(train)
        for begin in range(0, len(order), size):
            chosen = order[begin:begin + size]
            offsets = rng.integers(0, mels.shape[2] - CROP + 1, len(chosen))
            batch = np.stack([(mels[row, :, offset:offset + CROP].astype(np.float32) - mean) / std
                              for row, offset in zip(chosen, offsets, strict=True)])[:, None]
            inputs = torch.from_numpy(masked(batch, rng)).to(device)
            expected = torch.from_numpy(targets[chosen]).to(device)
            with torch.autocast(device.type, enabled=device.type == "cuda"):
                logits, _ = network(inputs)
                loss = nn.functional.cross_entropy(logits, expected, label_smoothing=0.1)
            optimiser.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.step(optimiser)
            scaler.update()
            schedule.step()

        predicted = classes[scores(network, mels, validation, mean, std, device).argmax(axis=1)]
        score = classification_metrics(labels[validation], predicted, list(classes))["macro_f1"]
        history.append(round(score, 4))
        print(f"epoch {epoch + 1}: validation macro F1 {score:.4f}", flush=True)
        if score > best:
            best, waited = score, 0
            best_state = {key: value.detach().cpu().clone() for key, value in network.state_dict().items()}
        else:
            waited += 1
            if patience is not None and waited >= patience:
                break

    network.load_state_dict(best_state)
    return network, {"train": train, "validation": validation, "test": test, "mean": mean, "std": std,
                     "history": history, "seconds": time.perf_counter() - started}


def export(network, path):
    exported = Exported(network.cpu().eval())
    example = torch.randn(2, 1, 128, CROP)
    options = {"input_names": ["mel"], "output_names": ["probabilities", "embedding"], "opset_version": 17,
               "dynamic_axes": {"mel": {0: "batch"}, "probabilities": {0: "batch"}, "embedding": {0: "batch"}}}
    try:
        torch.onnx.export(exported, example, str(path), dynamo=False, **options)
    except TypeError:
        torch.onnx.export(exported, example, str(path), **options)


def bundle(out, network, run, mels, labels, ids, classes, source, sha256, device):
    out = Path(out)
    (out / "resnet18").mkdir(parents=True, exist_ok=True)
    export(network, out / "resnet18" / "model.onnx")
    (out / "resnet18" / "model.json").write_text(json.dumps({
        "kind": "spectrogram", "classes": [str(label) for label in classes], "mean": run["mean"], "std": run["std"],
        "crop": CROP}), encoding="utf-8")

    network.to(device)
    results = {}
    for part in ("validation", "test"):
        predicted = classes[scores(network, mels, run[part], run["mean"], run["std"], device).argmax(axis=1)]
        results[part] = classification_metrics(labels[run[part]], predicted, list(classes))
    test = run["test"]
    center = (mels.shape[2] - CROP) // 2
    joblib.dump({"X": mels[test, :, center:center + CROP], "y": labels[test], "ids": ids[test]}, out / "test.joblib")

    predicted = classes[scores(network, mels, test, run["mean"], run["std"], device).argmax(axis=1)]
    counts = {str(label): int(count) for label, count in zip(*np.unique(predicted, return_counts=True), strict=True)}
    summary = {
        "task": "genre_cnn",
        "source": source,
        "sha256": sha256,
        "rows": {part: int(len(run[part])) for part in ("train", "validation", "test")},
        "environment": {"python": platform.python_version(), "torch": torch.__version__,
                        "torchvision": torchvision.__version__, "librosa": librosa.__version__,
                        "numpy": np.__version__},
        "drift_reference": {name: reference_bins(values) for name, values in
                            spectrogram_profile(mels[run["train"][:3000]]).items()},
        "best": "resnet18",
        "results": [{
            "candidate": "resnet18",
            "train_rows": int(len(run["train"])),
            "fit_seconds": round(run["seconds"], 1),
            "params": {"backbone": "resnet18 (ImageNet)", "crop_frames": CROP, "batch_size": 64, "max_lr": 1e-3,
                       "epochs_run": len(run["history"]), "best_epoch": int(np.argmax(run["history"])) + 1},
            "validation": results["validation"],
            "test": results["test"],
            "predictions": {"counts": counts},
            "history": run["history"],
        }],
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2, default=str), encoding="utf-8")
    print(f"test macro F1 {results['test']['macro_f1']:.4f}, accuracy {results['test']['accuracy']:.4f}", flush=True)
    return summary


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--cache", default="cache")
    parser.add_argument("--limit", type=int)
    parser.add_argument("--epochs", type=int, default=30)
    args = parser.parse_args(argv)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device {device}", flush=True)
    metadata = download(FMA_METADATA, args.cache)
    archive = download(FMA_SMALL, args.cache)
    genres = small_genres(metadata)
    started = time.perf_counter()
    mels, labels, ids = spectrograms(archive, genres, args.limit)
    print(f"{len(ids)} spectrograms in {time.perf_counter() - started:.0f}s", flush=True)
    classes = np.array(sorted(set(GENRES) & set(labels)))
    network, run = fit(mels, labels, ids, classes, device, epochs=args.epochs)
    bundle(args.out, network, run, mels, labels, ids, classes, "FMA small: audio and tracks.csv",
           digest(archive, metadata), device)


if __name__ == "__main__":
    main()
