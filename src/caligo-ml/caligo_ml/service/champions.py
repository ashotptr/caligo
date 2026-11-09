import json
from dataclasses import dataclass
from pathlib import Path

import joblib

from caligo_ml.tasks import TASKS


@dataclass
class Champion:
    task: str
    version: int
    model: object
    meta: dict
    rules: object | None


class Champions:
    def __init__(self, root):
        self.folder = Path(root) / "models"
        self.loaded = {}

    def refresh(self):
        for task in TASKS:
            pointer = self.folder / task / "champion.json"
            if not pointer.exists():
                continue
            version = json.loads(pointer.read_text(encoding="utf-8"))["version"]
            current = self.loaded.get(task)
            if current is not None and current.version == version:
                continue
            folder = self.folder / task / f"v{version}"
            rules = folder / "rules.joblib"
            self.loaded[task] = Champion(
                task, version, joblib.load(folder / "model.joblib"),
                json.loads((folder / "meta.json").read_text(encoding="utf-8")),
                joblib.load(rules) if rules.exists() else None)
        return self

    def get(self, task):
        return self.loaded.get(task)

    def versions(self, tasks=None):
        return {task: champion.version for task, champion in self.loaded.items() if tasks is None or task in tasks}
