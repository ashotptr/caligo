import argparse
import json
import os
import platform
import time
from pathlib import Path

import joblib
import numpy as np
import torch
import transformers
from sklearn.metrics import average_precision_score
from transformers import AutoModelForSequenceClassification, AutoTokenizer, get_linear_schedule_with_warmup

from caligo_ml.datasets import toxicity_data
from caligo_ml.deep.onnx_models import OnnxTextClassifier
from caligo_ml.evaluation import binary_metrics, frozen_split, reference_bins
from caligo_ml.features.text import drift_profile

BASE = "distilbert-base-uncased"
MAX_LENGTH = 128
SEED = 42


def encode(tokenizer, texts):
    return tokenizer([str(text) for text in texts], truncation=True, max_length=MAX_LENGTH, padding=True,
                     return_tensors="pt")


def chances(model, tokenizer, texts, device, size=128):
    model.eval()
    scores = []
    with torch.no_grad():
        for start in range(0, len(texts), size):
            batch = {key: value.to(device) for key, value in encode(tokenizer, texts[start:start + size]).items()}
            with torch.autocast(device.type, enabled=device.type == "cuda"):
                logits = model(**batch).logits
            scores.append(torch.softmax(logits.float(), dim=1)[:, 1].cpu())
    return torch.cat(scores).numpy()


def fit(model, tokenizer, texts, labels, validation, device, epochs=2, size=32):
    rng = np.random.default_rng(SEED)
    optimiser = torch.optim.AdamW(model.parameters(), lr=3e-5, weight_decay=0.01)
    steps = epochs * int(np.ceil(len(texts) / size))
    schedule = get_linear_schedule_with_warmup(optimiser, int(0.06 * steps), steps)
    scaler = torch.amp.GradScaler(enabled=device.type == "cuda")
    best, best_state, history = -1.0, None, []
    started = time.perf_counter()

    for epoch in range(epochs):
        model.train()
        order = rng.permutation(len(texts))
        for begin in range(0, len(order), size):
            chosen = order[begin:begin + size]
            batch = {key: value.to(device) for key, value in encode(tokenizer, texts[chosen]).items()}
            expected = torch.from_numpy(labels[chosen]).to(device)
            with torch.autocast(device.type, enabled=device.type == "cuda"):
                loss = torch.nn.functional.cross_entropy(model(**batch).logits, expected)
            optimiser.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.step(optimiser)
            scaler.update()
            schedule.step()

        score = average_precision_score(validation[1], chances(model, tokenizer, validation[0], device))
        history.append(round(float(score), 4))
        print(f"epoch {epoch + 1}: validation PR-AUC {score:.4f}", flush=True)
        if score > best:
            best = score
            best_state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}

    model.load_state_dict(best_state)
    return history, time.perf_counter() - started


def export(model, tokenizer, folder):
    from onnxruntime.quantization import QuantType, quantize_dynamic

    folder.mkdir(parents=True, exist_ok=True)
    model = model.cpu().eval().float()
    model.config.return_dict = False
    example = encode(tokenizer, ["an example comment", "a second, somewhat longer example comment"])
    full = folder / "model.fp32.onnx"
    options = {"input_names": ["input_ids", "attention_mask"], "output_names": ["logits"], "opset_version": 17,
               "dynamic_axes": {"input_ids": {0: "batch", 1: "tokens"}, "attention_mask": {0: "batch", 1: "tokens"},
                                "logits": {0: "batch"}}}
    arguments = (example["input_ids"], example["attention_mask"])
    try:
        torch.onnx.export(model, arguments, str(full), dynamo=False, **options)
    except TypeError:
        torch.onnx.export(model, arguments, str(full), **options)
    quantize_dynamic(str(full), str(folder / "model.onnx"), weight_type=QuantType.QInt8)
    full.unlink()
    (folder / "tokenizer.json").write_text(tokenizer.backend_tokenizer.to_str(), encoding="utf-8")
    (folder / "model.json").write_text(json.dumps({"kind": "text", "max_length": MAX_LENGTH, "classes": [0, 1]}),
                                       encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--cache", default="cache")
    parser.add_argument("--limit", type=int)
    parser.add_argument("--epochs", type=int, default=2)
    args = parser.parse_args(argv)

    torch.manual_seed(SEED)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device {device}", flush=True)
    data = toxicity_data(args.source)
    split = frozen_split(data.ids)
    train, validation, test = (np.flatnonzero(split == part)[:args.limit] for part in ("train", "validation", "test"))

    tokenizer = AutoTokenizer.from_pretrained(BASE)
    model = AutoModelForSequenceClassification.from_pretrained(BASE, num_labels=2).to(device)
    history, seconds = fit(model, tokenizer, data.X[train], data.y[train],
                           (data.X[validation], data.y[validation]), device, epochs=args.epochs)
    validation_scores = chances(model, tokenizer, data.X[validation], device)

    out = Path(args.out)
    export(model, tokenizer, out / "distilbert")
    served = OnnxTextClassifier((out / "distilbert" / "model.onnx").read_bytes(),
                                (out / "distilbert" / "tokenizer.json").read_text(encoding="utf-8"), MAX_LENGTH,
                                batch_size=64)
    served.threads = os.cpu_count() or 1
    test_scores = served.predict_proba(data.X[test])[:, 1]
    joblib.dump({"X": data.X[test], "y": data.y[test], "ids": data.ids[test]}, out / "test.joblib")

    summary = {
        "task": "toxicity",
        "source": data.source,
        "sha256": data.sha256,
        "rows": {"train": int(len(train)), "validation": int(len(validation)), "test": int(len(test))},
        "environment": {"python": platform.python_version(), "torch": torch.__version__,
                        "transformers": transformers.__version__, "numpy": np.__version__},
        "drift_reference": {name: reference_bins(values) for name, values in drift_profile(data.X[train]).items()},
        "best": "distilbert",
        "results": [{
            "candidate": "distilbert",
            "train_rows": int(len(train)),
            "fit_seconds": round(seconds, 1),
            "params": {"base": BASE, "max_length": MAX_LENGTH, "epochs": args.epochs, "learning_rate": 3e-5,
                       "batch_size": 32, "quantized": "int8 dynamic"},
            "validation": binary_metrics(data.y[validation], validation_scores),
            "test": binary_metrics(data.y[test], test_scores),
            "predictions": {"score": reference_bins(test_scores)},
            "history": history,
        }],
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2, default=str), encoding="utf-8")
    print(f"test PR-AUC with the int8 model {summary['results'][0]['test']['pr_auc']:.4f}", flush=True)


if __name__ == "__main__":
    main()
