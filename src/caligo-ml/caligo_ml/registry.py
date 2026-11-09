import json
import uuid
from importlib.metadata import version
from pathlib import Path

import joblib
import mlflow
from mlflow import MlflowClient
from mlflow.exceptions import MlflowException
from mlflow.models import infer_signature

from caligo_ml.deep.onnx_models import OnnxModel, OnnxSpectrogramClassifier, OnnxTextClassifier
from caligo_ml.evaluation import promotion
from caligo_ml.tasks import TASKS
from caligo_ml.training import environment


def numeric(prefix, metrics):
    return {f"{prefix}_{key}": float(value) for key, value in metrics.items()
            if isinstance(value, int | float) and not isinstance(value, bool)}


def requirements(environment):
    return [f"{name}=={version}" for name, version in environment.items() if name != "python"]


def bundle_model(folder):
    if (folder / "model.joblib").exists():
        return joblib.load(folder / "model.joblib")
    spec = json.loads((folder / "model.json").read_text(encoding="utf-8"))
    weights = (folder / "model.onnx").read_bytes()
    if spec["kind"] == "text":
        return OnnxTextClassifier(weights, (folder / "tokenizer.json").read_text(encoding="utf-8"),
                                  spec["max_length"], tuple(spec["classes"]))
    return OnnxSpectrogramClassifier(weights, tuple(spec["classes"]), spec["mean"], spec["std"], spec["crop"])


def served_requirements(model, trained):
    if not isinstance(model, OnnxModel):
        return requirements(trained)
    return requirements(environment()) + [f"{name}=={version(name)}" for name in ("onnxruntime", "tokenizers")]


def signature(task, model, X):
    return infer_signature(X[:5], model.predict(X[:5])) if task.inputs == "audio" else None


class Registry:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        mlflow.set_tracking_uri(f"sqlite:///{(self.root / 'mlflow.db').as_posix()}")
        self.client = MlflowClient()

    def experiment(self, name):
        found = self.client.get_experiment_by_name(name)
        if found is not None:
            return found.experiment_id
        return self.client.create_experiment(name, artifact_location=(self.root / "mlartifacts" / name).as_uri())

    def alias(self, name, alias):
        try:
            return self.client.get_model_version_by_alias(name, alias)
        except MlflowException:
            return None

    def log_candidates(self, task, summary, bundle, test):
        experiment_id = self.experiment(task.name)
        batch = uuid.uuid4().hex
        logged = {}
        for result in summary["results"]:
            with mlflow.start_run(experiment_id=experiment_id, run_name=result["candidate"]) as run:
                model = bundle_model(bundle / result["candidate"])
                mlflow.log_params(result["params"])
                mlflow.log_metrics(numeric("validation", result["validation"]) | numeric("test", result["test"])
                                   | {"fit_seconds": result["fit_seconds"]})
                mlflow.set_tags({
                    "candidate": result["candidate"],
                    "bundle": batch,
                    "source": summary["source"],
                    "data_sha256": summary["sha256"],
                    "train_rows": result["train_rows"],
                    **{f"environment.{name}": value for name, value in summary["environment"].items()},
                })
                mlflow.log_dict(result, "result.json")
                mlflow.log_dict(summary["drift_reference"], "drift_reference.json")
                info = mlflow.sklearn.log_model(
                    model, name="model", serialization_format=mlflow.sklearn.SERIALIZATION_FORMAT_CLOUDPICKLE,
                    pip_requirements=served_requirements(model, summary["environment"]),
                    signature=signature(task, model, test["X"]))
                mlflow.set_tag("model_uri", info.model_uri)
                logged[result["candidate"]] = (run.info.run_id, info.model_uri)
        return logged

    def register(self, bundle, candidate=None):
        bundle = Path(bundle)
        summary = json.loads((bundle / "summary.json").read_text(encoding="utf-8"))
        task = TASKS[summary["task"]]
        test = joblib.load(bundle / "test.joblib")
        logged = self.log_candidates(task, summary, bundle, test)

        best = next(result for result in summary["results"] if result["candidate"] == (candidate or summary["best"]))
        run_id, uri = logged[best["candidate"]]
        version = mlflow.register_model(uri, task.name).version
        score = best["test"][task.metric]
        for key, value in {"candidate": best["candidate"], "run_id": run_id, "data_sha256": summary["sha256"],
                           task.metric: score}.items():
            self.client.set_model_version_tag(task.name, version, key, str(value))

        decision = self.gate(task, test, score, summary["sha256"])
        self.client.set_registered_model_alias(task.name, "challenger", version)
        self.client.set_model_version_tag(task.name, version, "gate", decision.reason)
        if decision.promote:
            self.promote(task.name, version)
        return version, decision

    def gate(self, task, test, challenger, data_sha256):
        current = self.alias(task.name, "champion")
        champion = None
        if current is not None and current.tags.get("data_sha256") == data_sha256 and task.metric in current.tags:
            champion = float(current.tags[task.metric])
        elif current is not None:
            model = mlflow.sklearn.load_model(f"models:/{task.name}/{current.version}")
            champion = task.evaluate(model, test["X"], test["y"])[task.metric]
        return promotion(challenger, champion, task.minimum, task.higher_is_better)

    def promote(self, name, version, reason=None):
        if reason:
            self.client.set_model_version_tag(name, str(version), "promoted_because", reason)
        self.client.set_registered_model_alias(name, "champion", str(version))
        return self.export(name)

    def export(self, name):
        current = self.alias(name, "champion")
        run_id = current.tags["run_id"]
        result = mlflow.artifacts.load_dict(f"runs:/{run_id}/result.json")
        model = mlflow.sklearn.load_model(f"models:/{name}/{current.version}")
        folder = self.root / "models" / name / f"v{current.version}"
        folder.mkdir(parents=True, exist_ok=True)
        joblib.dump(model, folder / "model.joblib")
        meta = {
            "task": name,
            "version": int(current.version),
            "candidate": result["candidate"],
            "metric": TASKS[name].metric,
            "test": result["test"],
            "classes": [str(label) for label in getattr(model, "classes_", [])],
            "data_sha256": current.tags["data_sha256"],
            "drift_reference": mlflow.artifacts.load_dict(f"runs:/{run_id}/drift_reference.json"),
            "prediction_reference": result["predictions"],
        }
        rules = self.rules(name, run_id) or self.earlier_rules(folder)
        if rules is not None:
            joblib.dump(rules, folder / "rules.joblib")
        (folder / "meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
        (folder.parent / "champion.json").write_text(json.dumps({"version": int(current.version)}), encoding="utf-8")
        return folder

    def earlier_rules(self, folder):
        earlier = [path for path in folder.parent.glob("v*/rules.joblib") if path.parent != folder]
        if TASKS[folder.parent.name].inputs != "text" or not earlier:
            return None
        return joblib.load(max(earlier, key=lambda path: int(path.parent.name[1:])))

    def rules(self, name, run_id):
        if TASKS[name].inputs != "text":
            return None
        run = self.client.get_run(run_id)
        batch = run.data.tags.get("bundle")
        if batch is None:
            return None
        found = self.client.search_runs(
            [run.info.experiment_id], filter_string=f"tags.bundle = '{batch}' and tags.candidate = 'ripper'")
        return mlflow.sklearn.load_model(found[0].data.tags["model_uri"]) if found else None
