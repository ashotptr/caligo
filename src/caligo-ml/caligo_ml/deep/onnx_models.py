import math

import numpy as np
from sklearn.base import BaseEstimator, ClassifierMixin

from caligo_ml.algorithms.softmax_regression import softmax


class OnnxModel(ClassifierMixin, BaseEstimator):
    def __sklearn_is_fitted__(self):
        return True

    def session(self):
        if getattr(self, "_session", None) is None:
            import onnxruntime

            options = onnxruntime.SessionOptions()
            options.intra_op_num_threads = getattr(self, "threads", 1)
            self._session = onnxruntime.InferenceSession(self.model, options, providers=["CPUExecutionProvider"])
        return self._session

    def __getstate__(self):
        state = self.__dict__.copy()
        state.pop("_session", None)
        state.pop("_tokenizer", None)
        return state

    def predict(self, X):
        probabilities = self.predict_proba(X)
        return self.classes_[np.argmax(probabilities, axis=1)]


class OnnxSpectrogramClassifier(OnnxModel):
    def __init__(self, model=b"", classes=(), mean=0.0, std=1.0, crop=431):
        self.model = model
        self.classes = classes
        self.mean = mean
        self.std = std
        self.crop = crop
        self.classes_ = np.asarray(classes)

    def crops(self, mel):
        mel = np.asarray(mel, dtype=np.float32)
        if mel.shape[1] < self.crop:
            mel = np.pad(mel, ((0, 0), (0, self.crop - mel.shape[1])), constant_values=float(mel.min()))
        count = max(1, math.ceil(mel.shape[1] / self.crop))
        starts = np.linspace(0, mel.shape[1] - self.crop, count).astype(int)
        return np.stack([mel[:, start:start + self.crop] for start in starts])[:, None]

    def run(self, mels):
        probabilities, embeddings = [], []
        for mel in mels:
            batch = ((self.crops(mel) - self.mean) / self.std).astype(np.float32)
            chances, embedding = self.session().run(["probabilities", "embedding"], {"mel": batch})
            probabilities.append(chances.mean(axis=0))
            embeddings.append(embedding.mean(axis=0))
        return np.array(probabilities), np.array(embeddings)

    def predict_proba(self, X):
        return self.run(X)[0]

    def embed(self, X):
        return self.run(X)[1]


class OnnxTextClassifier(OnnxModel):
    def __init__(self, model=b"", tokenizer="", max_length=128, classes=(0, 1), batch_size=32):
        self.model = model
        self.tokenizer = tokenizer
        self.max_length = max_length
        self.classes = classes
        self.batch_size = batch_size
        self.classes_ = np.asarray(classes)

    def tokens(self):
        if getattr(self, "_tokenizer", None) is None:
            from tokenizers import Tokenizer

            self._tokenizer = Tokenizer.from_str(self.tokenizer)
            self._tokenizer.enable_truncation(self.max_length)
            self._tokenizer.enable_padding()
        return self._tokenizer

    def predict_proba(self, X):
        texts = [str(text) for text in X]
        order = np.argsort([len(text) for text in texts], kind="stable")
        chances = np.empty((len(texts), len(self.classes_)))
        for start in range(0, len(texts), self.batch_size):
            rows = order[start:start + self.batch_size]
            encoded = self.tokens().encode_batch([texts[row] for row in rows])
            feeds = {"input_ids": np.array([item.ids for item in encoded], dtype=np.int64),
                     "attention_mask": np.array([item.attention_mask for item in encoded], dtype=np.int64)}
            chances[rows] = softmax(self.session().run(["logits"], feeds)[0])
        return chances
