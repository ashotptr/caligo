import base64
import io
import json
import re
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
JIGSAW = {"competitions": ["jigsaw-toxic-comment-classification-challenge"], "arguments": ["--source", "/kaggle/input"]}
YOUTUBE = {"datasets": ["datasnaek/youtube-new"], "arguments": ["--youtube", "/kaggle/input"]}
SOURCES = {"toxicity": JIGSAW, "toxicity_bert": JIGSAW, "demo_stats": YOUTUBE}
DEEP = {"genre_cnn": "caligo_ml.deep.cnn", "toxicity_bert": "caligo_ml.deep.text", "demo_stats": "caligo_ml.demo.stats"}
GPU = {"genre_cnn", "toxicity_bert"}

BOOTSTRAP = '''
SOURCE = Path("/tmp/caligo-ml")
zipfile.ZipFile(io.BytesIO(base64.b64decode(PACKAGE))).extractall(SOURCE)
subprocess.run([sys.executable, "-m", "pip", "install", "--quiet", "uv"], check=True)
subprocess.run([sys.executable, "-m", "uv", "venv", "--python", "3.13", "/tmp/venv"], check=True)
subprocess.run([sys.executable, "-m", "uv", "pip", "install", "--quiet", "--python", "/tmp/venv/bin/python",
                "-r", str(SOURCE / "requirements-train.txt")], check=True)
subprocess.run(["/tmp/venv/bin/python", "-m", "caligo_ml", "train", TASK, "--out", "/kaggle/working/bundle",
                "--cache", "/tmp/data", *ARGUMENTS], check=True, cwd=SOURCE)
'''

DEEP_BOOTSTRAP = '''
SOURCE = Path("/tmp/caligo-ml")
zipfile.ZipFile(io.BytesIO(base64.b64decode(PACKAGE))).extractall(SOURCE)
subprocess.run([sys.executable, "-m", "pip", "install", "--quiet", "onnx", "onnxruntime", "tokenizers",
                "wittgenstein==0.3.5"], check=True)
subprocess.run([sys.executable, "-m", MODULE, "--out", "/kaggle/working/bundle", "--cache", "/tmp/data",
                *ARGUMENTS], check=True, cwd=SOURCE)
'''


def package():
    files = [*sorted((PROJECT / "caligo_ml").rglob("*.py")), *sorted((PROJECT / "caligo_ml").rglob("*.mako")),
             PROJECT / "requirements-train.txt",
             *sorted((PROJECT / "tests" / "data").rglob("*.csv"))]
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in files:
            bundle.write(path, path.relative_to(PROJECT).as_posix())
    return base64.b64encode(buffer.getvalue()).decode()


def slug(task):
    return f"caligo-ml-{task.replace('_', '-')}"


def build(task, user, folder, gpu=False, competitions=None, arguments=None):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    defaults = SOURCES.get(task, {})
    competitions = defaults.get("competitions", []) if competitions is None else competitions
    arguments = [*defaults.get("arguments", []), *(arguments or [])]
    module = DEEP.get(task)
    constants = f"PACKAGE = {package()!r}\nTASK = {task!r}\nMODULE = {module!r}\nARGUMENTS = {list(arguments)!r}\n"
    imports = "".join(f"import {name}\n" for name in ("base64", "io", "subprocess", "sys", "zipfile"))
    script = imports + "from pathlib import Path\n\n" + constants + (DEEP_BOOTSTRAP if module else BOOTSTRAP)
    (folder / "kernel.py").write_text(script, encoding="utf-8")
    metadata = {
        "id": f"{user}/{slug(task)}",
        "title": slug(task),
        "code_file": "kernel.py",
        "language": "python",
        "kernel_type": "script",
        "is_private": True,
        "enable_gpu": gpu or task in GPU,
        "enable_internet": True,
        "dataset_sources": list(defaults.get("datasets", [])),
        "competition_sources": list(competitions),
        "kernel_sources": [],
    }
    (folder / "kernel-metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return folder


def cli(*arguments):
    executable = shutil.which("kaggle") or str(Path(sys.executable).with_name("kaggle"))
    return subprocess.run([executable, *arguments], check=True, capture_output=True, text=True).stdout


def username():
    match = re.search(r"username:\s*(\S+)", cli("config", "view"))
    if match is None:
        raise RuntimeError("The Kaggle CLI has no username. Put the API token in ~/.kaggle/kaggle.json.")
    return match.group(1)


def wait(kernel, poll=60, settle=600):
    started = time.monotonic()
    active = False
    while True:
        status = cli("kernels", "status", kernel).lower()
        active = active or "queued" in status or "running" in status
        if active or time.monotonic() - started > settle:
            if "complete" in status:
                return
            if "error" in status or "cancel" in status:
                raise RuntimeError(f"Kaggle run {kernel} failed: {status.strip()}")
        time.sleep(poll)


def run_all(tasks, work, register, user=None, say=print):
    work = Path(work)
    user = user or username()
    started = time.monotonic()

    def note(text):
        say(f"{time.monotonic() - started:6.0f}s {text}")

    for task in tasks:
        cli("kernels", "push", "-p", str(build(task, user, work / task / "kernel")))
        note(f"pushed {task}")
    results = {}
    for task in tasks:
        kernel = f"{user}/{slug(task)}"
        output = work / task / "output"
        try:
            wait(kernel)
        except RuntimeError as error:
            note(f"{task} failed on Kaggle: {error}")
            cli("kernels", "output", kernel, "-p", str(output))
            results[task] = "failed"
            continue
        cli("kernels", "output", kernel, "-p", str(output))
        results[task] = register(output / "bundle")
        note(f"{task}: {results[task]}")
    return results


def run(task, work, gpu=False, competitions=None, arguments=None, user=None):
    work = Path(work)
    user = user or username()
    kernel = f"{user}/{slug(task)}"
    cli("kernels", "push", "-p", str(build(task, user, work / "kernel", gpu, competitions, arguments)))
    wait(kernel)
    output = work / "output"
    cli("kernels", "output", kernel, "-p", str(output))
    return output / "bundle"
