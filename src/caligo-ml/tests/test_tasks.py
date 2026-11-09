from caligo_ml.tasks import TASKS


def test_every_task_names_a_metric_it_reports():
    reported = {"multiclass": "macro_f1", "binary": "pr_auc", "regression": "mae"}

    for task in TASKS.values():
        assert task.metric == reported[task.kind]
        assert task.higher_is_better is (task.kind != "regression")
        assert task.inputs in ("audio", "text", "spectrogram")


def test_moderation_tasks_share_their_candidates():
    assert TASKS["spam"].candidates().keys() == TASKS["toxicity"].candidates().keys()
