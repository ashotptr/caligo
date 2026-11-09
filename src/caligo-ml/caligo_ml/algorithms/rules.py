import numpy as np
import pandas as pd
from sklearn.base import BaseEstimator, ClassifierMixin
from sklearn.feature_selection import SelectKBest, chi2
from sklearn.utils.multiclass import check_classification_targets
from sklearn.utils.validation import check_is_fitted
from wittgenstein import RIPPER

from caligo_ml.features.text import word_presence


class WordRuleClassifier(ClassifierMixin, BaseEstimator):
    def __init__(self, max_features=300, vocabulary=20_000, k=2, prune_size=0.33, max_rules=None,
                 random_state=None):
        self.max_features = max_features
        self.vocabulary = vocabulary
        self.k = k
        self.prune_size = prune_size
        self.max_rules = max_rules
        self.random_state = random_state

    def fit(self, X, y):
        y = np.asarray(y)
        check_classification_targets(y)
        self.classes_ = np.unique(y)
        if len(self.classes_) != 2:
            raise ValueError("WordRuleClassifier needs exactly two classes.")

        flags = (y == self.classes_[1]).astype(int)
        self.vectorizer_ = word_presence(max_features=self.vocabulary)
        words = self.vectorizer_.fit_transform(X)
        self.selector_ = SelectKBest(chi2, k=min(self.max_features, words.shape[1])).fit(words, flags)
        self.names_ = self.vectorizer_.get_feature_names_out()[self.selector_.get_support()]
        self.ripper_ = RIPPER(k=self.k, prune_size=self.prune_size, max_rules=self.max_rules,
                              random_state=self.random_state)
        self.ripper_.fit(self._frame(words), flags, pos_class=1)
        self.rules_ = [str(rule) for rule in self.ripper_.ruleset_.rules]
        return self

    def _frame(self, words):
        return pd.DataFrame(self.selector_.transform(words).toarray(), columns=self.names_)

    def _words(self, X):
        check_is_fitted(self)
        return self._frame(self.vectorizer_.transform(X))

    def predict_proba(self, X):
        return np.asarray(self.ripper_.predict_proba(self._words(X)), dtype=float)

    def predict(self, X):
        flagged = np.asarray(self.ripper_.predict(self._words(X)), dtype=bool)
        return self.classes_[flagged.astype(int)]

    def reasons(self, X):
        _, fired = self.ripper_.predict(self._words(X), give_reasons=True)
        return [[str(rule) for rule in rules] for rules in fired]
