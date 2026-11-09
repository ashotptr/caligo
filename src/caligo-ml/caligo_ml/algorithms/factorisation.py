import numpy as np


def ridge_rows(owners, count, design, target, regularisation):
    size = design.shape[1]
    normal = np.zeros((count, size, size))
    np.add.at(normal, owners, design[:, :, None] * design[:, None, :])
    normal += regularisation * np.eye(size)
    right = np.zeros((count, size))
    np.add.at(right, owners, design * target[:, None])
    return np.linalg.solve(normal, right[..., None])[..., 0]


class AlternatingLeastSquares:
    def __init__(self, rank=3, regularisation=5.0, iterations=50, tol=1e-6, random_state=None):
        self.rank = rank
        self.regularisation = regularisation
        self.iterations = iterations
        self.tol = tol
        self.random_state = random_state

    def fit(self, users, items, ratings):
        users, items = np.asarray(users, dtype=int), np.asarray(items, dtype=int)
        ratings = np.asarray(ratings, dtype=float)
        if not (len(users) == len(items) == len(ratings)) or len(ratings) == 0:
            raise ValueError("users, items and ratings must be non-empty and the same length.")
        rng = np.random.default_rng(self.random_state)
        self.n_users_, self.n_items_ = int(users.max()) + 1, int(items.max()) + 1
        self.mean_ = float(ratings.mean())
        user_factors = rng.normal(0, 0.1, (self.n_users_, self.rank))
        item_factors = rng.normal(0, 0.1, (self.n_items_, self.rank))
        user_bias, item_bias = np.zeros(self.n_users_), np.zeros(self.n_items_)
        self.loss_history_ = []

        for _ in range(self.iterations):
            design = np.column_stack([np.ones(len(items)), item_factors[items]])
            solved = ridge_rows(users, self.n_users_, design, ratings - self.mean_ - item_bias[items],
                                self.regularisation)
            user_bias, user_factors = solved[:, 0], solved[:, 1:]

            design = np.column_stack([np.ones(len(users)), user_factors[users]])
            solved = ridge_rows(items, self.n_items_, design, ratings - self.mean_ - user_bias[users],
                                self.regularisation)
            item_bias, item_factors = solved[:, 0], solved[:, 1:]

            error = ratings - (self.mean_ + user_bias[users] + item_bias[items]
                               + np.sum(user_factors[users] * item_factors[items], axis=1))
            penalty = (np.sum(user_bias ** 2) + np.sum(item_bias ** 2) + np.sum(user_factors ** 2)
                       + np.sum(item_factors ** 2))
            self.loss_history_.append(float(np.sum(error ** 2) + self.regularisation * penalty))
            if len(self.loss_history_) > 1 and self.loss_history_[-2] - self.loss_history_[-1] < self.tol:
                break

        self.user_bias_, self.user_factors_ = user_bias, user_factors
        self.item_bias_, self.item_factors_ = item_bias, item_factors
        self.n_iter_ = len(self.loss_history_)
        self.residual_std_ = float(np.std(error))
        return self

    def predict(self, users, items):
        users, items = np.asarray(users, dtype=int), np.asarray(items, dtype=int)
        return (self.mean_ + self.user_bias_[users] + self.item_bias_[items]
                + np.sum(self.user_factors_[users] * self.item_factors_[items], axis=1))

    def predict_for(self, bias, factors, items):
        items = np.asarray(items, dtype=int)
        return self.mean_ + bias + self.item_bias_[items] + self.item_factors_[items] @ np.asarray(factors)
