import numpy as np

from caligo_ml.service import scoring
from caligo_ml.service.champions import Champion


class Sure:
    classes_ = np.array([0, 1])

    def __init__(self, score):
        self.score = score

    def predict_proba(self, texts):
        return np.array([[1 - self.score, self.score] for _ in texts])


class Rules:
    def __init__(self, fired):
        self.fired = fired

    def reasons(self, texts):
        return [list(self.fired) for _ in texts]


class Only:
    def __init__(self, champion):
        self.champion = champion

    def get(self, task):
        return self.champion if task == self.champion.task else None


def judge(score, rules, task="spam"):
    champion = Champion(task, 1, Sure(score), {}, rules)
    return scoring.comments(Only(champion), ["a comment"])[0]


def test_a_confident_score_needs_a_rule_that_fired():
    assert judge(0.9, Rules([]))["flagged"] is False
    assert judge(0.9, Rules(["[subscribe=1]"])) == {
        "spam": 0.9, "toxicity": None, "flagged": True, "reasons": ["spam: [subscribe=1]"]}


def test_a_rule_alone_is_not_enough():
    assert judge(0.3, Rules(["[subscribe=1]"]))["flagged"] is False


def test_toxicity_flags_on_the_score_and_still_shows_a_rule_that_fired():
    assert judge(0.9, Rules([]), "toxicity")["flagged"] is True
    assert judge(0.9, Rules(["[idiot=1]"]), "toxicity")["reasons"] == ["toxicity: [idiot=1]"]
    assert judge(0.3, Rules(["[idiot=1]"]), "toxicity")["flagged"] is False


def test_without_rules_the_score_decides():
    assert judge(0.9, None)["flagged"] is True
    assert judge(0.3, None)["flagged"] is False
