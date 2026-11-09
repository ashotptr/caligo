import ast
import base64
import io
import json
import zipfile

import pytest

from caligo_ml import kaggle


def test_the_kernel_carries_the_package_and_its_settings(tmp_path):
    folder = kaggle.build("genre", "someone", tmp_path, gpu=True, competitions=["jigsaw"], arguments=["--source", "x"])
    metadata = json.loads((folder / "kernel-metadata.json").read_text())
    script = (folder / "kernel.py").read_text()
    constants = {node.targets[0].id: ast.literal_eval(node.value) for node in ast.parse(script).body
                 if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant | ast.List)}
    names = zipfile.ZipFile(io.BytesIO(base64.b64decode(constants["PACKAGE"]))).namelist()

    assert metadata["id"] == "someone/caligo-ml-genre"
    assert metadata["title"] == "caligo-ml-genre"
    assert metadata["is_private"] is True
    assert metadata["enable_gpu"] is True
    assert metadata["enable_internet"] is True
    assert metadata["competition_sources"] == ["jigsaw"]
    assert constants["TASK"] == "genre"
    assert constants["ARGUMENTS"] == ["--source", "x"]
    assert {"caligo_ml/__main__.py", "caligo_ml/tasks.py", "requirements-train.txt"} <= set(names)
    assert "tests/data/youtube-spam/shakira.csv" in names
    assert not any(name.startswith(".venv") for name in names)


def test_toxicity_brings_the_jigsaw_competition_by_default(tmp_path):
    folder = kaggle.build("toxicity", "someone", tmp_path)
    metadata = json.loads((folder / "kernel-metadata.json").read_text())

    assert metadata["competition_sources"] == ["jigsaw-toxic-comment-classification-challenge"]
    assert "ARGUMENTS = ['--source', '/kaggle/input']" in (folder / "kernel.py").read_text()


def test_waiting_ignores_the_previous_run_until_the_new_one_starts(monkeypatch):
    answers = iter(["complete", "queued", "running", "complete"])
    asked = []
    monkeypatch.setattr(kaggle, "cli", lambda *arguments: asked.append(arguments) or next(answers))
    monkeypatch.setattr(kaggle.time, "sleep", lambda seconds: None)

    kaggle.wait("someone/caligo-ml-genre")

    assert len(asked) == 4


def test_waiting_reports_a_failed_run(monkeypatch):
    answers = iter(["running", "error"])
    monkeypatch.setattr(kaggle, "cli", lambda *arguments: next(answers))
    monkeypatch.setattr(kaggle.time, "sleep", lambda seconds: None)

    with pytest.raises(RuntimeError, match="failed"):
        kaggle.wait("someone/caligo-ml-genre")


def test_the_statistics_kernel_runs_without_a_gpu(tmp_path):
    folder = kaggle.build("demo_stats", "someone", tmp_path, arguments=["--films", "[]"])
    metadata = json.loads((folder / "kernel-metadata.json").read_text())

    assert metadata["id"] == "someone/caligo-ml-demo-stats"
    assert metadata["enable_gpu"] is False
    assert metadata["dataset_sources"] == ["datasnaek/youtube-new"]
    assert "ARGUMENTS = ['--youtube', '/kaggle/input', '--films', '[]']" in (folder / "kernel.py").read_text()
    assert "MODULE = 'caligo_ml.demo.stats'" in (folder / "kernel.py").read_text()


def test_training_several_tasks_queues_every_run_before_waiting(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(kaggle, "cli", lambda *arguments: calls.append(arguments[1]) or "")
    monkeypatch.setattr(kaggle, "wait", lambda kernel: calls.append("wait"))

    results = kaggle.run_all(["spam", "genre"], tmp_path, lambda bundle: f"registered {bundle.parent.parent.name}",
                             user="someone", say=lambda line: None)

    assert calls == ["push", "push", "wait", "output", "wait", "output"]
    assert results == {"spam": "registered spam", "genre": "registered genre"}


def test_a_failed_run_is_reported_and_the_others_still_register(monkeypatch, tmp_path):
    def wait(kernel):
        if kernel.endswith("spam"):
            raise RuntimeError("error")

    monkeypatch.setattr(kaggle, "cli", lambda *arguments: "")
    monkeypatch.setattr(kaggle, "wait", wait)

    results = kaggle.run_all(["spam", "genre"], tmp_path, lambda bundle: "registered", user="someone",
                             say=lambda line: None)

    assert results == {"spam": "failed", "genre": "registered"}
