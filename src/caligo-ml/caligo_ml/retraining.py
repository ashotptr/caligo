from prefect import flow, serve

from caligo_ml import kaggle
from caligo_ml.registry import Registry


@flow(name="retrain")
def retrain(task: str, root: str, work: str = "kaggle-runs"):
    version, decision = Registry(root).register(kaggle.run(task, f"{work}/{task}"))
    return {"task": task, "version": version, "promoted": decision.promote, "reason": decision.reason}


if __name__ == "__main__":
    serve(retrain.to_deployment(name="on-demand"))
