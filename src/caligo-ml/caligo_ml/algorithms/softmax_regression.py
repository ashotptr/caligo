import numpy as np
from sklearn.base import BaseEstimator, ClassifierMixin
from sklearn.utils.class_weight import compute_class_weight
from sklearn.utils.multiclass import check_classification_targets
from sklearn.utils.validation import check_is_fitted, validate_data

from caligo_ml.algorithms.steps import largest_eigenvalue


def softmax(scores):
    scores = scores - np.max(scores, axis=1, keepdims=True)
    exponents = np.exp(scores)
    return exponents / np.sum(exponents, axis=1, keepdims=True)


def with_bias(X):
    return np.insert(X, 0, 1, axis=1)


class SoftmaxRegression(ClassifierMixin, BaseEstimator):
    def __init__(self, learning_rate="auto", max_epochs=1000, tol=1e-6, patience=5, alpha=0.0, class_weight=None):
        self.learning_rate = learning_rate
        self.max_epochs = max_epochs
        self.tol = tol
        self.patience = patience
        self.alpha = alpha
        self.class_weight = class_weight

    def fit(self, X, y):
        X, y = validate_data(self, X, y, dtype=np.float64)
        check_classification_targets(y)
        self.classes_, targets = np.unique(y, return_inverse=True)
        weights = compute_class_weight(self.class_weight, classes=self.classes_, y=y)[targets][:, None]
        weights /= weights.mean()
        X = with_bias(X)
        one_hot = np.eye(len(self.classes_))[targets]
        decay = np.ones((X.shape[1], 1))
        decay[0] = 0
        if self.learning_rate == "auto":
            self.learning_rate_ = 1 / (largest_eigenvalue(X, weights) / 2 + self.alpha)
        else:
            self.learning_rate_ = self.learning_rate
        self.weights_ = np.zeros((X.shape[1], len(self.classes_)))
        self.loss_curve_ = []
        previous = np.inf
        best_loss, best = np.inf, self.weights_.copy()
        rising = 0

        for _ in range(self.max_epochs):
            probabilities = softmax(X @ self.weights_)
            loss = -np.sum(weights * one_hot * np.log(np.clip(probabilities, 1e-15, None))) / len(X)
            loss += self.alpha / 2 * np.sum((decay * self.weights_) ** 2)
            self.loss_curve_.append(float(loss))
            if loss < best_loss:
                best_loss, best = loss, self.weights_.copy()

            if abs(previous - loss) < self.tol:
                break

            rising = rising + 1 if loss > previous else 0
            if rising >= self.patience:
                break

            previous = loss
            gradient = X.T @ (weights * (one_hot - probabilities)) / len(X) - self.alpha * decay * self.weights_
            self.weights_ += self.learning_rate_ * gradient

        self.weights_ = best
        self.n_iter_ = len(self.loss_curve_)
        return self

    def predict_proba(self, X):
        check_is_fitted(self)
        X = validate_data(self, X, dtype=np.float64, reset=False)
        return softmax(with_bias(X) @ self.weights_)

    def predict(self, X):
        probabilities = self.predict_proba(X)
        return self.classes_[np.argmax(probabilities, axis=1)]
