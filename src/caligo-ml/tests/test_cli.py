from caligo_ml import __main__ as cli
from caligo_ml.service import jobs, state


def test_a_job_repeats_until_nothing_remains_then_the_next_runs(monkeypatch, capsys):
    details = iter([{"scored": 50, "remaining": 2}, {"scored": 2, "remaining": 0}, {"checks": 3}])
    seen = []

    class Quiet:
        @classmethod
        def from_env(cls):
            return cls()

        def close(self):
            seen.append("closed")

    monkeypatch.setattr(state, "State", Quiet)
    monkeypatch.setattr(jobs, "run", lambda current, name: seen.append(name) or next(details))

    cli.main(["job", "score_songs", "drift_report"])

    assert seen == ["score_songs", "score_songs", "drift_report", "closed"]
    assert capsys.readouterr().out.count('"job"') == 3
