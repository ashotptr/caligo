from collections.abc import Callable
from dataclasses import dataclass

import numpy as np
from lightgbm import LGBMClassifier, LGBMRegressor
from sklearn.calibration import CalibratedClassifierCV
from sklearn.decomposition import PCA
from sklearn.linear_model import LinearRegression, LogisticRegression, Ridge
from sklearn.model_selection import GridSearchCV, StratifiedKFold
from sklearn.multioutput import MultiOutputRegressor
from sklearn.naive_bayes import BernoulliNB, MultinomialNB
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import FunctionTransformer, PolynomialFeatures, StandardScaler
from sklearn.svm import SVC
from sklearn.tree import DecisionTreeClassifier

from caligo_ml.algorithms import (
    LinearRegressionGD,
    SoftmaxRegression,
    TwoLayerNetClassifier,
    TwoLayerNetRegressor,
    WordRuleClassifier,
)
from caligo_ml.evaluation import binary_metrics, classification_metrics, regression_metrics
from caligo_ml.features import audio, text
from caligo_ml.features.text import tfidf_words, word_presence

SEED = 42
ROW_LIMITS = {"svm": 12_000, "svm_rbf": 12_000, "ripper": 20_000}


def capped():
    return FunctionTransformer(np.clip, kw_args={"a_min": -5.0, "a_max": 5.0})


def folds():
    return StratifiedKFold(n_splits=3, shuffle=True, random_state=SEED)


def svm(scoring, **options):
    calibrated = CalibratedClassifierCV(SVC(kernel="rbf", random_state=SEED, **options), ensemble=False, cv=3)
    grid = {"calibratedclassifiercv__estimator__C": [0.1, 1, 10],
            "calibratedclassifiercv__estimator__gamma": ["scale", "auto"]}
    return GridSearchCV(make_pipeline(StandardScaler(), calibrated), grid, cv=folds(), scoring=scoring, n_jobs=-1)


def polynomial(final, components=40):
    return make_pipeline(StandardScaler(), capped(), PCA(n_components=components, random_state=SEED),
                         PolynomialFeatures(degree=2, include_bias=False), StandardScaler(), final)


def genre_candidates():
    return {
        "softmax_regression": make_pipeline(
            StandardScaler(), SoftmaxRegression(max_epochs=3000, tol=1e-6, alpha=1e-3, class_weight="balanced")),
        "two_layer_net": make_pipeline(StandardScaler(), TwoLayerNetClassifier(
            hidden_size=256, alpha=1e-3, batch_size=128, class_weight="balanced", random_state=SEED)),
        "logistic_regression": make_pipeline(
            StandardScaler(), LogisticRegression(C=0.1, max_iter=3000, class_weight="balanced")),
        "svm": svm("f1_macro", class_weight="balanced"),
        "decision_tree": DecisionTreeClassifier(
            max_depth=12, min_samples_leaf=20, class_weight="balanced", random_state=SEED),
        "lightgbm": LGBMClassifier(
            n_estimators=600, learning_rate=0.05, num_leaves=63, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, class_weight="balanced", random_state=SEED, verbose=-1),
    }


def mood_candidates():
    return {
        "linear_regression_gd": make_pipeline(StandardScaler(), LinearRegressionGD(epochs=3000)),
        "multiple_linear_regression": make_pipeline(StandardScaler(), LinearRegression()),
        "polynomial_regression": polynomial(Ridge(alpha=30.0)),
        "two_layer_net": make_pipeline(
            StandardScaler(), capped(),
            TwoLayerNetRegressor(hidden_size=128, alpha=1e-3, batch_size=128, random_state=SEED)),
        "lightgbm": MultiOutputRegressor(LGBMRegressor(
            n_estimators=500, learning_rate=0.03, num_leaves=31, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, random_state=SEED, verbose=-1)),
    }


def instrumental_candidates():
    return {
        "logistic_regression": make_pipeline(
            StandardScaler(), LogisticRegression(C=0.1, max_iter=3000, class_weight="balanced")),
        "polynomial_logistic_regression": polynomial(
            LogisticRegression(C=0.05, max_iter=3000, class_weight="balanced")),
        "svm_rbf": svm("average_precision", class_weight="balanced"),
        "softmax_regression": make_pipeline(
            StandardScaler(), SoftmaxRegression(max_epochs=3000, tol=1e-6, alpha=1e-3, class_weight="balanced")),
        "two_layer_net": make_pipeline(StandardScaler(), TwoLayerNetClassifier(
            hidden_size=128, alpha=1e-3, batch_size=128, class_weight="balanced", random_state=SEED)),
    }


def moderation_candidates():
    return {
        "naive_bayes": make_pipeline(tfidf_words(), MultinomialNB(alpha=0.1)),
        "bernoulli_naive_bayes": make_pipeline(word_presence(), BernoulliNB(alpha=0.5)),
        "decision_tree": make_pipeline(
            word_presence(max_features=2000),
            DecisionTreeClassifier(max_depth=10, min_samples_leaf=3, class_weight="balanced", random_state=SEED)),
        "ripper": WordRuleClassifier(max_features=300, random_state=SEED),
        "logistic_regression": make_pipeline(
            tfidf_words(), LogisticRegression(C=4.0, max_iter=3000, class_weight="balanced")),
    }


@dataclass(frozen=True)
class Task:
    name: str
    kind: str
    inputs: str
    metric: str
    higher_is_better: bool
    minimum: float
    candidates: Callable[[], dict]

    def drift_profile(self, X):
        profiles = {"audio": audio.drift_profile, "text": text.drift_profile, "spectrogram": audio.spectrogram_profile}
        return profiles[self.inputs](X)

    def evaluate(self, model, X, y):
        if self.kind == "regression":
            return regression_metrics(y, model.predict(X), n_features=X.shape[1])
        if self.kind == "binary":
            return binary_metrics(y, model.predict_proba(X)[:, list(model.classes_).index(1)])
        return classification_metrics(y, model.predict(X), labels=list(model.classes_))


TASKS = {
    "genre": Task("genre", "multiclass", "audio", "macro_f1", True, 0.45, genre_candidates),
    "mood": Task("mood", "regression", "audio", "mae", False, 0.2, mood_candidates),
    "instrumental": Task("instrumental", "binary", "audio", "pr_auc", True, 0.8, instrumental_candidates),
    "spam": Task("spam", "binary", "text", "pr_auc", True, 0.85, moderation_candidates),
    "toxicity": Task("toxicity", "binary", "text", "pr_auc", True, 0.6, moderation_candidates),
    "genre_cnn": Task("genre_cnn", "multiclass", "spectrogram", "macro_f1", True, 0.45, dict),
}
