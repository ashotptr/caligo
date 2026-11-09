# **caligo-ml**
This is the machine learning part of Caligo. It listens to the songs in the Thesaurarium and says what they sound like, finds songs that sound alike, picks a colour for each album from its cover, and checks comments for spam and toxicity.

The models are trained on public datasets, locally or on Kaggle, and served by a small API.

### What it adds to the site
* **Sound:** Each song gets a genre, a mood (energy, valence and danceability) and whether it is instrumental or vocal.
* **More like this:** Songs that sound alike, found from what a CNN hears in each song.
* **Album colours:** An accent colour for each album page, taken from its cover.
* **Moderation:** Comments are checked for spam and toxicity. Admins see the flagged comments and the rule that flagged them.

### `caligo_ml/algorithms`
Algorithms written from scratch with NumPy as scikit-learn estimators: softmax regression, linear regression by gradient descent, a two-layer neural network, matrix factorisation (ALS) and RIPPER rules. They started as exercises in [MLAlgorithms](https://github.com/ashotptr/MLAlgorithms) and pass scikit-learn's own estimator checks.

### `caligo_ml/tasks.py`
The tasks and the models each one tries. The best model on the test data:

| Task | Trained on | Best model |
|---|---|---|
| Genre (8 genres) | FMA | LightGBM on audio features (macro F1 0.597) and a ResNet18 CNN on spectrograms (macro F1 0.677) |
| Mood | FMA | LightGBM (MAE 0.118) |
| Instrumental or vocal | FMA | Softmax regression (PR-AUC 0.926) |
| Spam | YouTube Spam Collection | Logistic regression (PR-AUC 0.983) |
| Toxicity | Jigsaw Toxic Comments | DistilBERT (PR-AUC 0.928) |

### `caligo_ml/registry.py`
Every training run is logged to MLflow. A new model replaces the current one only when it scores better on the same test data.

### `caligo_ml/service`
The API, in FastAPI. It scores new songs and comments on a schedule, and checks whether the songs and comments it sees drift away from the training data.

---

## Usage
### Dependencies
**Python 3.13**. A Kaggle account for training on Kaggle.

### Setup

```
cd src/caligo-ml
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt

```

On Linux and macOS the folder is `.venv/bin` instead of `.venv/Scripts`.

### Tests

```
.venv/Scripts/ruff check .
.venv/Scripts/python -m pytest

```

### Train
Train every model of a task, then register the best one:

```
.venv/Scripts/python -m caligo_ml train spam --out bundles/spam
.venv/Scripts/python -m caligo_ml register bundles/spam --root ../../data/dev/ml

```

On Kaggle, after signing in once with `.venv/Scripts/kaggle auth login`:

```
.venv/Scripts/python -m caligo_ml kaggle genre --root ../../data/dev/ml
.venv/Scripts/python -m caligo_ml kaggle-all genre mood instrumental spam toxicity --root ../../data/dev/ml

```

* `genre_cnn` and `toxicity_bert` run on a Kaggle GPU.
* Toxicity needs the Jigsaw competition rules accepted on kaggle.com.
* `promote <task> <version>` brings back an earlier model.

### Run
The service starts with the rest of the site. To score the songs right away:

```
docker compose -f ../../deploy/compose/docker-compose.yml run --rm --no-deps caligo-ml python -m caligo_ml job score_songs

```

### Datasets
* FMA: CC BY 4.0.
* YouTube Spam Collection: CC BY 4.0.
* Jigsaw Toxic Comment Classification: CC0, with the comment text under CC BY-SA 3.0.
* MovieLens 32M: research use.
* Last.fm-1K: non-commercial research use.
* Trending YouTube Video Statistics: CC0.
