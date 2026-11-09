import numpy as np
from sklearn.base import BaseEstimator, RegressorMixin
from sklearn.utils.validation import check_is_fitted, validate_data

from caligo_ml.algorithms.steps import largest_eigenvalue


class LinearRegressionGD(RegressorMixin, BaseEstimator):
    def __init__(self, learning_rate="auto", epochs=1000, tol=1e-9):
        self.learning_rate = learning_rate
        self.epochs = epochs
        self.tol = tol

    def fit(self, X, y):
        X, y = validate_data(self, X, y, dtype=np.float64, multi_output=True, y_numeric=True)
        targets = y.reshape(len(y), -1)
        n = len(X)
        if self.learning_rate == "auto":
            self.learning_rate_ = 1 / (2 * largest_eigenvalue(np.column_stack([X, np.ones(n)])))
        else:
            self.learning_rate_ = self.learning_rate
        weights = np.zeros((X.shape[1], targets.shape[1]))
        bias = np.zeros(targets.shape[1])
        self.cost_history_ = []

        for _ in range(self.epochs):
            error = X @ weights + bias - targets
            cost = float(np.sum(error ** 2) / n)

            if not np.isfinite(cost) or (self.cost_history_ and cost > self.cost_history_[0]):
                raise ValueError("Gradient descent diverged: scale the features or lower learning_rate.")

            self.cost_history_.append(cost)
            if len(self.cost_history_) > 1 and self.cost_history_[-2] - cost < self.tol:
                break

            weights -= self.learning_rate_ * (2 / n) * (X.T @ error)
            bias -= self.learning_rate_ * (2 / n) * error.sum(axis=0)

        self.n_iter_ = len(self.cost_history_)
        self.coef_ = weights.T if y.ndim > 1 else weights[:, 0]
        self.intercept_ = bias if y.ndim > 1 else float(bias[0])
        return self

    def predict(self, X):
        check_is_fitted(self)
        X = validate_data(self, X, dtype=np.float64, reset=False)
        return X @ self.coef_.T + self.intercept_

    def __sklearn_tags__(self):
        tags = super().__sklearn_tags__()
        tags.target_tags.multi_output = True
        return tags
