from pathlib import Path

import numpy as np
import pandas as pd
import pytest
from sklearn.base import clone
from sklearn.datasets import load_diabetes, load_digits, make_blobs, make_circles, make_classification
from sklearn.linear_model import LinearRegression, LogisticRegression
from sklearn.metrics import recall_score
from sklearn.model_selection import train_test_split
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from sklearn.utils.estimator_checks import parametrize_with_checks

from caligo_ml.algorithms import (
    AlternatingLeastSquares,
    LinearRegressionGD,
    SoftmaxRegression,
    TwoLayerNetClassifier,
    TwoLayerNetRegressor,
    WordRuleClassifier,
)

SPAM = Path(__file__).parent / "data" / "youtube-spam"


@parametrize_with_checks([SoftmaxRegression(), LinearRegressionGD(), TwoLayerNetClassifier(), TwoLayerNetRegressor()])
def test_follows_the_scikit_learn_contract(estimator, check):
    check(estimator)


def digits():
    X, y = load_digits(return_X_y=True)
    return train_test_split(X, y, test_size=0.25, stratify=y, random_state=42)


def diabetes():
    X, y = load_diabetes(return_X_y=True)
    return StandardScaler().fit_transform(X), y


def test_softmax_regression_agrees_with_scikit_learn():
    X_train, X_test, y_train, y_test = digits()
    ours = make_pipeline(StandardScaler(), SoftmaxRegression(learning_rate=0.5, max_epochs=3000, tol=1e-7))
    theirs = make_pipeline(StandardScaler(), LogisticRegression(max_iter=5000))
    ours.fit(X_train, y_train)
    theirs.fit(X_train, y_train)

    assert ours.score(X_test, y_test) >= theirs.score(X_test, y_test) - 0.02
    assert np.mean(ours.predict(X_test) == theirs.predict(X_test)) >= 0.95


def test_softmax_regression_stops_once_the_loss_settles():
    X_train, _, y_train, _ = digits()
    model = SoftmaxRegression(learning_rate=0.5, max_epochs=5000, tol=1e-3)
    model.fit(StandardScaler().fit_transform(X_train), y_train)

    assert model.n_iter_ < 5000
    assert np.all(np.diff(model.loss_curve_) < 0)


def test_softmax_regression_steps_safely_on_raw_features():
    X, y = make_blobs(centers=3, cluster_std=20, random_state=0)
    model = SoftmaxRegression(max_epochs=20_000, tol=1e-7).fit(X, y)
    reference = LogisticRegression(max_iter=5000).fit(X, y)

    assert np.all(np.diff(model.loss_curve_) <= 1e-12)
    assert np.mean(model.predict(X) == reference.predict(X)) > 0.9


@pytest.mark.parametrize("model", [SoftmaxRegression(max_epochs=2000), TwoLayerNetClassifier(random_state=0)])
def test_balanced_class_weights_find_more_of_the_rare_class(model):
    X, y = make_classification(n_samples=2000, n_features=10, weights=[0.95, 0.05], class_sep=0.8, random_state=1)
    X = StandardScaler().fit_transform(X)
    plain = clone(model).fit(X, y)
    balanced = clone(model).set_params(class_weight="balanced").fit(X, y)

    assert recall_score(y, balanced.predict(X)) > recall_score(y, plain.predict(X)) + 0.1


def test_softmax_regression_keeps_string_labels():
    X_train, X_test, y_train, _ = digits()
    names = np.array(["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"])
    model = make_pipeline(StandardScaler(), SoftmaxRegression()).fit(X_train, names[y_train])

    assert set(model.predict(X_test)) <= set(names)
    np.testing.assert_allclose(model.predict_proba(X_test).sum(axis=1), 1.0)


def test_linear_regression_gd_reaches_least_squares():
    X, y = diabetes()
    ours = LinearRegressionGD(epochs=50_000, tol=0).fit(X, y)
    exact = LinearRegression().fit(X, y)

    assert ours.score(X, y) == pytest.approx(exact.score(X, y), abs=1e-4)
    np.testing.assert_allclose(ours.predict(X), exact.predict(X), atol=0.5)
    assert np.all(np.diff(ours.cost_history_) <= 1e-9 * ours.cost_history_[0])


def test_linear_regression_gd_picks_a_step_that_converges_on_raw_features():
    X, y = load_diabetes(return_X_y=True)
    model = LinearRegressionGD(epochs=20_000).fit(X * 1000, y)

    assert np.all(np.diff(model.cost_history_) <= 1e-9 * model.cost_history_[0])
    assert model.score(X * 1000, y) > 0.4


def test_linear_regression_gd_fits_several_targets_at_once():
    X, y = diabetes()
    model = LinearRegressionGD(epochs=5000).fit(X, np.column_stack([y, 2 * y + 1]))

    assert model.coef_.shape == (2, X.shape[1])
    np.testing.assert_allclose(model.coef_[1], 2 * model.coef_[0], rtol=1e-6)
    assert model.intercept_[1] == pytest.approx(2 * model.intercept_[0] + 1)


def test_linear_regression_gd_reports_divergence():
    X, y = diabetes()
    with pytest.raises(ValueError, match="diverged"):
        LinearRegressionGD(learning_rate=5.0).fit(X, y)


@pytest.mark.parametrize("activation", ["sigmoid", "relu"])
def test_two_layer_net_learns_xor(activation):
    X = np.array([[0, 0], [0, 1], [1, 0], [1, 1]] * 50, dtype=float)
    y = np.array([0, 1, 1, 0] * 50)
    net = TwoLayerNetClassifier(hidden_size=8, activation=activation, learning_rate=0.1, max_epochs=300,
                                validation_fraction=0, random_state=42).fit(X, y)

    assert net.predict(X[:4]).tolist() == [0, 1, 1, 0]


def test_two_layer_net_draws_a_boundary_a_linear_model_cannot():
    X, y = make_circles(n_samples=400, noise=0.1, factor=0.5, random_state=42)
    X_train, X_test, y_train, y_test = train_test_split(X, y, test_size=0.25, random_state=42)
    linear = SoftmaxRegression().fit(X_train, y_train).score(X_test, y_test)
    net = TwoLayerNetClassifier(hidden_size=32, max_epochs=300, random_state=42).fit(X_train, y_train)

    assert linear < 0.7
    assert net.score(X_test, y_test) > 0.9


def test_two_layer_net_regressor_fits_a_parabola():
    rng = np.random.default_rng(42)
    x = np.linspace(0, 5, 300).reshape(-1, 1)
    y = 2 * x[:, 0] ** 2 - 3 * x[:, 0] + rng.normal(0, 3, len(x))
    model = make_pipeline(StandardScaler(), TwoLayerNetRegressor(hidden_size=32, max_epochs=500, random_state=42))

    assert model.fit(x, y).score(x, y) > 0.85


def test_two_layer_net_regressor_survives_extreme_inputs():
    X, y = diabetes()
    X = X.copy()
    X[:5] *= 1e4
    model = TwoLayerNetRegressor(hidden_size=32, max_epochs=50, random_state=0).fit(X, y)

    assert np.isfinite(model.predict(X)).all()


def test_two_layer_net_is_repeatable_with_a_seed():
    X_train, X_test, y_train, _ = digits()
    first = TwoLayerNetClassifier(random_state=7).fit(X_train / 16, y_train).predict_proba(X_test / 16)
    second = TwoLayerNetClassifier(random_state=7).fit(X_train / 16, y_train).predict_proba(X_test / 16)

    np.testing.assert_array_equal(first, second)


def test_rule_classifier_explains_its_flags():
    comments = pd.concat(pd.read_csv(path) for path in sorted(SPAM.glob("*.csv")))
    model = WordRuleClassifier(random_state=42).fit(comments.content, comments.spam)
    texts = ["Please subscribe to my channel", "Check out http://example.com/free", "Her voice is beautiful"]
    reasons = model.reasons(texts)

    assert model.rules_
    assert model.predict(texts).tolist() == [1, 1, 0]
    assert any(word in " ".join(reasons[0]) for word in ("subscribe", "channel"))
    assert "httpurl" in " ".join(reasons[1])
    assert reasons[2] == []


def low_rank_ratings(seed, users=300, items=20, rank=2, observed=0.4):
    rng = np.random.default_rng(seed)
    full = (3 + rng.normal(0, 0.5, (users, 1)) + rng.normal(0, 0.5, (1, items))
            + rng.normal(0, 1, (users, rank)) @ rng.normal(0, 1, (rank, items)) + rng.normal(0, 0.1, (users, items)))
    mask = rng.random((users, items)) < observed
    rows, columns = np.nonzero(mask)
    return rows, columns, full[rows, columns], full, mask


def test_alternating_least_squares_fills_in_unseen_ratings():
    users, items, ratings, full, mask = low_rank_ratings(0)
    model = AlternatingLeastSquares(rank=2, regularisation=1.0, random_state=0).fit(users, items, ratings)
    hidden_users, hidden_items = np.nonzero(~mask)
    truth = full[hidden_users, hidden_items]

    error = np.sqrt(np.mean((model.predict(hidden_users, hidden_items) - truth) ** 2))
    baseline = np.sqrt(np.mean((ratings.mean() - truth) ** 2))
    assert error < 0.35 * baseline


def test_alternating_least_squares_never_raises_its_objective():
    users, items, ratings, _, _ = low_rank_ratings(1)
    model = AlternatingLeastSquares(rank=3, iterations=20, tol=0, random_state=1).fit(users, items, ratings)
    history = model.loss_history_

    assert len(history) == 20
    assert all(later <= earlier + 1e-6 for earlier, later in zip(history, history[1:], strict=False))


def test_alternating_least_squares_scores_a_new_user_from_factors():
    users, items, ratings, _, _ = low_rank_ratings(2)
    model = AlternatingLeastSquares(rank=2, random_state=2).fit(users, items, ratings)

    scored = model.predict_for(model.user_bias_[5], model.user_factors_[5], [3, 4])
    assert scored == pytest.approx(model.predict([5, 5], [3, 4]))
