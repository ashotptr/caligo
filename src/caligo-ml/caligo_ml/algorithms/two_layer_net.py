import numpy as np
from scipy.special import expit
from sklearn.base import BaseEstimator, ClassifierMixin, RegressorMixin
from sklearn.utils import check_random_state
from sklearn.utils.class_weight import compute_class_weight
from sklearn.utils.multiclass import check_classification_targets
from sklearn.utils.validation import check_is_fitted, validate_data

from caligo_ml.algorithms.softmax_regression import softmax


class TwoLayerNet(BaseEstimator):
    def __init__(self, hidden_size=64, activation="relu", learning_rate=0.01, momentum=0.9, alpha=1e-4,
                 batch_size=64, max_epochs=200, validation_fraction=0.1, patience=10, tol=1e-4,
                 random_state=None, max_norm=5.0):
        self.hidden_size = hidden_size
        self.activation = activation
        self.learning_rate = learning_rate
        self.momentum = momentum
        self.alpha = alpha
        self.batch_size = batch_size
        self.max_epochs = max_epochs
        self.validation_fraction = validation_fraction
        self.patience = patience
        self.tol = tol
        self.random_state = random_state
        self.max_norm = max_norm

    def _hidden(self, X):
        Z1 = X @ self.W1_ + self.b1_
        return np.maximum(Z1, 0) if self.activation == "relu" else expit(Z1)

    def _scores(self, X):
        return self._hidden(X) @ self.W2_ + self.b2_

    def _gradients(self, X, targets, weights):
        A1 = self._hidden(X)
        dZ2 = weights[:, None] * (self._output(A1 @ self.W2_ + self.b2_) - targets) / len(X)
        dW2 = A1.T @ dZ2 + self.alpha * self.W2_
        db2 = dZ2.sum(axis=0)
        dA1 = dZ2 @ self.W2_.T
        dZ1 = dA1 * (A1 > 0) if self.activation == "relu" else dA1 * A1 * (1 - A1)
        dW1 = X.T @ dZ1 + self.alpha * self.W1_
        db1 = dZ1.sum(axis=0)
        return dW1, db1, dW2, db2

    def _fit(self, X, targets, weights):
        if self.activation not in ("relu", "sigmoid"):
            raise ValueError(f"activation must be 'relu' or 'sigmoid', got {self.activation!r}.")

        rng = check_random_state(self.random_state)
        rows = rng.permutation(len(X))
        held_out = int(len(X) * self.validation_fraction)
        monitor, train = (rows[:held_out], rows[held_out:]) if held_out > 0 else (rows, rows)

        gain = 2.0 if self.activation == "relu" else 1.0
        self.W1_ = rng.standard_normal((X.shape[1], self.hidden_size)) * np.sqrt(gain / X.shape[1])
        self.b1_ = np.zeros(self.hidden_size)
        self.W2_ = rng.standard_normal((self.hidden_size, targets.shape[1])) * np.sqrt(1.0 / self.hidden_size)
        self.b2_ = np.zeros(targets.shape[1])
        parameters = [self.W1_, self.b1_, self.W2_, self.b2_]
        velocities = [np.zeros_like(parameter) for parameter in parameters]

        best_loss, best, waited = np.inf, [parameter.copy() for parameter in parameters], 0
        self.loss_curve_ = []

        for _ in range(self.max_epochs):
            order = rng.permutation(train)
            for start in range(0, len(order), self.batch_size):
                batch = order[start:start + self.batch_size]
                gradients = self._gradients(X[batch], targets[batch], weights[batch])
                norm = np.sqrt(sum(float(np.sum(gradient ** 2)) for gradient in gradients))
                if self.max_norm is not None and norm > self.max_norm:
                    gradients = [gradient * (self.max_norm / norm) for gradient in gradients]
                for parameter, velocity, gradient in zip(parameters, velocities, gradients, strict=True):
                    velocity *= self.momentum
                    velocity -= self.learning_rate * gradient
                    parameter += velocity

            loss = self._loss(X[monitor], targets[monitor], weights[monitor])
            if not np.isfinite(loss):
                raise ValueError("Training diverged: scale the features or lower learning_rate.")

            self.loss_curve_.append(loss)
            if loss < best_loss - self.tol:
                best_loss, best, waited = loss, [parameter.copy() for parameter in parameters], 0
            else:
                waited += 1
                if waited >= self.patience:
                    break

        for parameter, kept in zip(parameters, best, strict=True):
            parameter[...] = kept

        self.n_iter_ = len(self.loss_curve_)
        return self


class TwoLayerNetClassifier(ClassifierMixin, TwoLayerNet):
    def __init__(self, hidden_size=64, activation="relu", learning_rate=0.01, momentum=0.9, alpha=1e-4,
                 batch_size=64, max_epochs=200, validation_fraction=0.1, patience=10, tol=1e-4,
                 random_state=None, max_norm=5.0, class_weight=None):
        super().__init__(hidden_size=hidden_size, activation=activation, learning_rate=learning_rate,
                         momentum=momentum, alpha=alpha, batch_size=batch_size, max_epochs=max_epochs,
                         validation_fraction=validation_fraction, patience=patience, tol=tol,
                         random_state=random_state, max_norm=max_norm)
        self.class_weight = class_weight

    def _output(self, scores):
        return softmax(scores)

    def _loss(self, X, targets, weights):
        probabilities = self._output(self._scores(X))
        losses = -np.sum(targets * np.log(np.clip(probabilities, 1e-15, None)), axis=1)
        return float(np.sum(weights * losses) / np.sum(weights))

    def fit(self, X, y):
        X, y = validate_data(self, X, y, dtype=np.float64)
        check_classification_targets(y)
        self.classes_, targets = np.unique(y, return_inverse=True)
        weights = compute_class_weight(self.class_weight, classes=self.classes_, y=y)[targets]
        return self._fit(X, np.eye(len(self.classes_))[targets], weights / weights.mean())

    def predict_proba(self, X):
        check_is_fitted(self)
        X = validate_data(self, X, dtype=np.float64, reset=False)
        return self._output(self._scores(X))

    def predict(self, X):
        probabilities = self.predict_proba(X)
        return self.classes_[np.argmax(probabilities, axis=1)]


class TwoLayerNetRegressor(RegressorMixin, TwoLayerNet):
    def _output(self, scores):
        return scores

    def _loss(self, X, targets, weights):
        return float(np.sum(weights * np.sum((self._scores(X) - targets) ** 2, axis=1)) / np.sum(weights) / 2)

    def fit(self, X, y):
        X, y = validate_data(self, X, y, dtype=np.float64, multi_output=True, y_numeric=True)
        targets = y.reshape(len(y), -1)
        spread = targets.std(axis=0)
        self.target_mean_ = targets.mean(axis=0)
        self.target_scale_ = np.where(spread > 0, spread, 1.0)
        self.multi_output_ = y.ndim > 1
        return self._fit(X, (targets - self.target_mean_) / self.target_scale_, np.ones(len(X)))

    def predict(self, X):
        check_is_fitted(self)
        X = validate_data(self, X, dtype=np.float64, reset=False)
        predictions = self._scores(X) * self.target_scale_ + self.target_mean_
        return predictions if self.multi_output_ else predictions[:, 0]

    def __sklearn_tags__(self):
        tags = super().__sklearn_tags__()
        tags.target_tags.multi_output = True
        return tags
