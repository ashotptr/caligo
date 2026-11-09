import argparse
import json
from pathlib import Path

from caligo_ml import datasets, kaggle
from caligo_ml.tasks import TASKS
from caligo_ml.training import train

LOADERS = {
    "genre": lambda args: datasets.genre_data(args.cache),
    "mood": lambda args: datasets.mood_data(args.cache),
    "instrumental": lambda args: datasets.instrumental_data(args.cache),
    "spam": lambda args: datasets.spam_data(args.source or datasets.YOUTUBE_SPAM),
    "toxicity": lambda args: datasets.toxicity_data(args.source),
}


JOB_NAMES = ["score_songs", "album_palettes", "moderate_backlog", "drift_report"]


def report(version, decision):
    outcome = "now the champion" if decision.promote else "the champion stays"
    print(f"registered version {version}: {decision.reason}, {outcome}")


def describe(version, decision):
    return f"version {version}, {decision.reason}, {'now the champion' if decision.promote else 'the champion stays'}"


def run_jobs(names):
    from caligo_ml.service import jobs
    from caligo_ml.service.state import State

    state = State.from_env()
    try:
        for name in names:
            while True:
                detail = jobs.run(state, name)
                print(json.dumps({"job": name, **detail}), flush=True)
                if not detail.get("remaining") or detail.get("error") or detail.get("skipped"):
                    break
    finally:
        state.close()


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m caligo_ml")
    commands = parser.add_subparsers(dest="command", required=True)

    local = commands.add_parser("train", help="train every candidate of a task and write a bundle")
    local.add_argument("task", choices=sorted(LOADERS))
    local.add_argument("--out", required=True)
    local.add_argument("--cache", default="cache")
    local.add_argument("--source")

    register = commands.add_parser("register", help="log a bundle to MLflow and run the promotion gate")
    register.add_argument("bundle")
    register.add_argument("--root", required=True)
    register.add_argument("--candidate", help="register this candidate instead of the best on validation")

    promote = commands.add_parser("promote", help="make a version the champion and export it, also for rollback")
    promote.add_argument("task", choices=sorted(TASKS))
    promote.add_argument("version")
    promote.add_argument("--root", required=True)
    promote.add_argument("--reason", help="why this version replaces the gate's choice, kept on the version")

    remote = commands.add_parser("kaggle", help="train on Kaggle, then register the bundle it returns")
    remote.add_argument("task", choices=sorted([*LOADERS, *kaggle.DEEP]))
    remote.add_argument("--root", required=True)
    remote.add_argument("--work", default="kaggle-runs")
    remote.add_argument("--gpu", action="store_true")
    remote.add_argument("--competition", action="append")
    remote.add_argument("--source")
    remote.add_argument("--films", help="catalog export whose films the demo statistics look up")
    remote.add_argument("--user", help="Kaggle username, when the CLI cannot tell")

    several = commands.add_parser("kaggle-all", help="push several training runs at once, then register each result")
    several.add_argument("tasks", nargs="+", choices=sorted(LOADERS))
    several.add_argument("--root", required=True)
    several.add_argument("--work", default="kaggle-runs")
    several.add_argument("--user", help="Kaggle username, when the CLI cannot tell")

    batch = commands.add_parser("job", help="run service jobs in order, repeating each until nothing remains")
    batch.add_argument("names", nargs="+", choices=JOB_NAMES)

    args = parser.parse_args(argv)
    if args.command == "train":
        train(TASKS[args.task], LOADERS[args.task](args), args.out)
        return
    if args.command == "job":
        run_jobs(args.names)
        return

    from caligo_ml.registry import Registry

    if args.command == "register":
        report(*Registry(args.root).register(args.bundle, args.candidate))
    elif args.command == "promote":
        print(f"exported {Registry(args.root).promote(args.task, args.version, args.reason)}")
    elif args.command == "kaggle-all":
        registry = Registry(args.root)
        kaggle.run_all(args.tasks, args.work, lambda bundle: describe(*registry.register(bundle)), args.user,
                       lambda line: print(line, flush=True))
    else:
        extra = ["--source", args.source] if args.source else []
        if args.films:
            titles = json.loads(Path(args.films).read_text(encoding="utf-8"))["titles"]
            films = [{"id": title["content_id"], "title": title["title"], "year": title.get("year")}
                     for title in titles if title["kind"] == "film"]
            extra += ["--films", json.dumps(films)]
        bundle = kaggle.run(args.task, args.work, args.gpu, args.competition, extra, args.user)
        if (bundle / "summary.json").exists():
            report(*Registry(args.root).register(bundle))
        else:
            print(f"downloaded {bundle}")


if __name__ == "__main__":
    main()
