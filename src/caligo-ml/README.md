# caligo-ml

Machine learning for Caligo: what a song sounds like (genre, mood, instrumental or vocal), which songs sound alike, album accent colours, and comment moderation. The models are trained on public datasets and then applied to Caligo's own songs and comments.

## Layout

| Path | What it holds |
|---|---|
| `caligo_ml/algorithms/` | Algorithms written from scratch with NumPy as scikit-learn estimators, so they work in pipelines, grid search and cross-validation |
| `caligo_ml/features/audio.py` | The 518 FMA audio features (librosa), computed the way FMA's `features.py` computes them, on 30 seconds from the middle of a song |
| `caligo_ml/features/text.py` | Comment cleaning (HTML, links, invisible characters) and word features |
| `caligo_ml/features/covers.py` | Cover palettes with K-Means on the pixels, and an accent colour that reads on a dark page |
| `caligo_ml/clustering.py` | K-Means with the elbow and silhouette reports, and cosine nearest neighbours for "More like this" |
| `caligo_ml/evaluation.py` | Metrics, PSI drift checks (0.1 watch, 0.25 drift), the hash-based frozen split, and the champion/challenger promotion rule |
| `caligo_ml/tasks.py` | Each task's input, metric, quality floor and candidate models |
| `caligo_ml/datasets.py` | Loaders for FMA, the YouTube Spam Collection and Jigsaw, each with the SHA-256 of its source files |
| `caligo_ml/training.py` | Trains every candidate of a task and writes a bundle: the models, their metrics, the test split and the drift reference |
| `caligo_ml/registry.py` | Logs a bundle to MLflow, registers the best candidate as the challenger, runs the gate, and exports the champion for serving |
| `caligo_ml/kaggle.py` | Runs the training on Kaggle and fetches the bundle |
| `caligo_ml/demo/` | Demo activity: statistics from MovieLens, Last.fm-1K and YouTube (`stats.py`, run on Kaggle), the catalog export (`catalog.py`) and the plan generator (`generate.py`) |
| `tests/` | Unit tests, scikit-learn's estimator checks, and the quality gate |

## The from-scratch algorithms

They started as exercises in [MLAlgorithms](https://github.com/ashotptr/MLAlgorithms) and were reworked for real data.

| Estimator | Started as | What changed |
|---|---|---|
| `SoftmaxRegression` | 1.19 | Any class labels (not only 0..k-1), class weights, L2 penalty that leaves the bias alone, clipped log, loss curve. The step size comes from the data, 1 / L with L = λmax(XᵀWX/n) / 2 + α, the most the cross-entropy can curve, so the loss always goes down. The early stopping (tolerance and patience) is kept, and the best weights are kept |
| `LinearRegressionGD` | 1.20 | Many features and several targets at once. The step size is 1 / (2 λmax) of XᵀX/n with the bias column, found by power iteration, so the cost always goes down. A fixed rate that diverges raises an error |
| `TwoLayerNetClassifier`, `TwoLayerNetRegressor` | 2.6 | Softmax output for any number of classes, or a linear output for regression. ReLU hidden layer with He initialisation, or sigmoid with LeCun initialisation, mini-batches with momentum, L2 penalty, class weights, early stopping on a held-out slice. The regressor standardises its targets |
| `WordRuleClassifier` | 3.5 | RIPPER (wittgenstein) on word-presence features. It says which rule flagged a comment |

`AlternatingLeastSquares` (`factorisation.py`) is new: biased matrix factorisation fitted by alternating ridge regressions, all users or all items solved in one batched NumPy call. Its objective never goes up, and on a rank-2 test matrix with 40% of the ratings shown it fills in the hidden ones with RMSE 0.25, against 1.51 for the mean. The demo generator draws film tastes from it.

All four NumPy estimators pass scikit-learn's own estimator checks (`parametrize_with_checks`), class weights included. The tests also check that softmax regression agrees with scikit-learn's logistic regression, that gradient descent reaches the least-squares solution, and that the two-layer net learns XOR and a boundary a linear model cannot draw.

## Tasks

| Task | Data | Metric | Candidates |
|---|---|---|---|
| Genre, the 8 genres of fma_small | FMA features and top genres, 46,317 tracks | macro F1 | softmax regression, two-layer net, logistic regression, RBF SVM with grid search, decision tree, LightGBM, all with balanced class weights |
| Mood (energy, valence, danceability) | FMA features and Echo Nest values, 13,129 tracks | MAE | gradient-descent linear regression, multiple linear regression, polynomial ridge regression, two-layer net, LightGBM |
| Instrumental or vocal | the same tracks, instrumentalness 0.5 or more | PR-AUC | logistic regression, polynomial logistic regression, RBF SVM, softmax regression, two-layer net |
| Spam | YouTube Spam Collection, 1,956 comments | PR-AUC | multinomial and Bernoulli naive Bayes, decision tree, RIPPER, logistic regression |
| Toxicity | Jigsaw Toxic Comment Classification, 159,571 comments | PR-AUC | the spam candidates |

## Training, registry and promotion

1. Every row gets a fixed place from a hash of its ID: 64% train, 16% validation, 20% test. A row never changes sides between runs, and new data only adds rows, so a test row is never trained on by any version.
2. `train` fits every candidate on the train rows and writes a bundle.
3. `register` logs each candidate as an MLflow run with its parameters, metrics, data hash and package versions. The candidate with the best validation score becomes a new version with the alias `challenger`.
4. The gate compares the challenger with the current `champion` on the same test rows. When the bundle comes from the same data file as the champion, the champion's stored test score is that comparison. With new data the gate scores the champion again on the new test rows. The challenger is promoted only if it clears the task's floor and beats the champion. The decision is written on the version.
5. A promoted version is exported to `data/<env>/ml/models/<task>/v<N>/` with its meta file (classes, test metrics, the drift reference of its training data and the spread of its predictions), and `champion.json` points to it. The serving image has no MLflow in it.
6. `promote <task> <version>` moves the champion alias by hand, which is also the rollback.

```
.venv\Scripts\python -m caligo_ml train spam --out bundles\spam
.venv\Scripts\python -m caligo_ml register bundles\spam --root ..\..\data\dev\ml
.venv\Scripts\python -m caligo_ml promote spam 1 --root ..\..\data\dev\ml
.venv\Scripts\mlflow ui --backend-store-uri sqlite:///../../data/dev/ml/mlflow.db --port 5001
```

## Results on the full data

Trained on Kaggle on 2026-11-07, scored on the frozen test rows. Each champion was picked on the validation rows.

| Task | Test rows | Champion | Others (test) |
|---|---|---|---|
| Genre, macro F1 (8 genres, chance 0.125) | 9,248 | LightGBM 0.597 (accuracy 0.713) | two-layer net 0.517, logistic regression 0.512, softmax regression 0.510, RBF SVM 0.494, decision tree 0.345 |
| Mood, MAE over energy, valence and danceability | 2,666 | LightGBM 0.118 (R² 0.61) | two-layer net 0.127 (R² 0.56), gradient-descent linear regression 0.128 (R² 0.47), multiple linear regression 0.128, polynomial ridge 0.144 |
| Instrumental, PR-AUC (base rate 0.71) | 2,666 | RBF SVM 0.930 | softmax regression 0.926, logistic regression 0.924, two-layer net 0.910, polynomial logistic regression 0.903 |
| Spam, PR-AUC | 358 | logistic regression 0.983 | naive Bayes 0.964, Bernoulli naive Bayes 0.959, decision tree 0.945, RIPPER 0.933 |
| Toxicity, PR-AUC (10% toxic) | 31,934 | logistic regression 0.878 | naive Bayes 0.798, Bernoulli naive Bayes 0.703, decision tree 0.555, RIPPER 0.311 |

The genre CNN (`genre_cnn`) is an ImageNet ResNet18 whose first layer takes one channel, fine-tuned on 10-second crops of 128-band log-mel spectrograms with time and frequency masking. It is trained on the 8,000 fma_small clips (7,994 decoded) on a Kaggle GPU and exported to ONNX (45 MB). A clip is scored as the average of three crops. Version 1 reached test macro F1 0.586 (accuracy 0.582, 1,622 test clips): Hip-Hop 0.77, Electronic 0.69, International 0.67, Folk 0.62, Instrumental 0.55, Experimental 0.53, Rock 0.46, Pop 0.40. Its validation score swung between 0.38 and 0.60 because early stopping ended the run at epoch 13, while the one-cycle learning rate was still near its peak. Later runs train all 30 epochs and keep the best validation epoch. Version 2 trained all 30 epochs and kept the best validation epoch, and reached test macro F1 0.677. The gate promoted it over version 1. On the site, a song's genre is the average of the feature model's and the CNN's probabilities, and "More like this" uses the CNN's 512-number embedding.

Toxicity has a deep candidate too: DistilBERT fine-tuned for 2 epochs on the 102K training comments (11 minutes on the GPU), exported to ONNX and quantised to int8 (67 MB). The int8 model scored test PR-AUC 0.928 (F1 0.842, precision 0.90, recall 0.79), against 0.878 for the logistic regression champion, and the gate promoted it. Its flags carry the reasons of the earlier RIPPER rules, because a text champion without rules keeps the newest rules of its task.

## On Caligo's own library

All 442 songs were scored on dev with every champion, at about 5 seconds a song on one CPU.

- **Genres:** Rock 171, Pop 80, Experimental 54, Folk 50, Instrumental 32, Hip-Hop 25, Electronic 25, International 3.
  - Nirvana's MTV Unplugged comes out as Rock, with Folk second.
  - Carla Morrison comes out mostly Folk and Pop.
  - Zimmer's Interstellar comes out Instrumental and Experimental, with low energy and low valence.
- **"More like this":**
  - "About A Girl" (live) brings "Come As You Are" (live), Radiohead's "Stop Whispering", and three more Nirvana live tracks.
  - "Cornfield Chase" brings more Zimmer and Muse's "Isolated System".
  - The radio folder holds copies of album songs, so the list skips repeats and copies of the song itself.
- **Drift against FMA:** 25 of 47 checks say drift. The biggest is loudness: `rmse` PSI 8.28, because Caligo's commercial masters are much louder than FMA's independent tracks. Genre predictions differ too (PSI 0.56 for the feature model, 0.75 for the CNN), since the library leans to Rock and Pop. Retraining on the same data would not close this gap. Admin genre corrections measure the real accuracy. Normalising loudness before computing features is the next improvement.
- **The SVM on unfamiliar songs:** the RBF SVM that decides instrumental or vocal gives the same 0.855 to 45 songs, mostly ambient film scores. An RBF kernel goes to zero far from every support vector, so the decision falls back to the bias. Those songs are instrumental, so the label is still right.
- **Speed on Docker Desktop:** MP3s are read in one go, then decoded from memory. Reading the Windows folder through the 9P share in small seeks took 41 seconds a song instead of 5.

Two things the first runs taught:

- The two-layer net regressor diverged on mood. A few FMA statistics, kurtosis in particular, sit hundreds of standard deviations out even after scaling. Gradients are now clipped to a norm of 5, and the mood net gets its inputs capped at ±5 standard deviations.
- RIPPER scored 0.153 on toxicity when it was given the 300 most frequent words: at 10% toxic, those are words like "the" and "you". It now gets the 300 words with the highest chi² against the label, out of a 20,000-word vocabulary, and writes rules such as `[fuck=1]` and `[...stupid=1]` (precision 0.94, recall 0.24). Spam moved from 0.908 to 0.918 on the unseen video.

## Training on Kaggle

Sign in once with `.venv\Scripts\kaggle auth login`. Then:

```
.venv\Scripts\python -m caligo_ml kaggle genre --root ..\..\data\dev\ml
```

It pushes a private kernel that carries this package as a zip. The kernel installs the pinned `requirements-train.txt` into a Python 3.13 environment with uv, so the models load the same way where they are served. `requirements-serve.txt` builds on the same pins. A dry run of the generated kernel in a `python:3.12-slim` container trained the spam candidates with the same scores as on Windows, and the models it wrote predict the same here (largest difference 1.7e-15). It downloads the data from the official sources and trains. The runner waits for the kernel, downloads the bundle and registers it. Toxicity needs the Jigsaw competition rules accepted on kaggle.com and `--competition jigsaw-toxic-comment-classification-challenge`.

## The service

`caligo_ml/service/` is a FastAPI app. On start it creates its database `caligoml` if it is missing, runs the Alembic migrations and loads each task's exported champion.

| Endpoint | Who calls it | What it does |
|---|---|---|
| `GET /health`, `GET /ready` | Docker, CI | Liveness, and readiness with the database check and the loaded model versions |
| `POST /predict/moderation` | anyone inside the network | Scores up to 100 comments for spam and toxicity, with the RIPPER rules behind each flag |
| `POST /predict/song` | anyone inside the network | Genre (top 3), energy, valence, danceability and instrumental from the 518 features |
| `POST /moderation/comments` | the API, when a comment is posted | Scores one catalog comment and stores the result |
| `GET /moderation/queue`, `POST /moderation/comments/{id}/label` | admins, through the API | Flagged comments, and an admin's verdict on one |
| `GET /songs?uri=`, `GET /albums?folder=` | the site, through nginx at `/api/ml/` | A song's analysis with "More like this", and an album's palette and accent colour |
| `POST /songs/label` | admins, through the API | A genre correction |
| `GET /monitoring` | admins, through the API | Model versions, the latest drift report, prediction counts, rejected inputs, accuracy on admin labels, job runs |
| `POST /models/reload` | the operator | Loads newly exported champions without a restart |

A comment is flagged when its score is 0.5 or more and one of the RIPPER rules trained with that model fired, so every flag shows the rule behind it. On comments from the unseen Shakira video this keeps precision at 1.000 (recall 0.787, against 0.993 and 0.851 for the score alone). On the 98 comments of the dev catalog the score alone flagged 10 harmless film comments such as "This one had me laughing out loud.", because "me" and "had" are spam words on YouTube. With the rule, only 3 leftover "Test comment" entries are flagged. The drift report had already pointed at this: comment length PSI 3.6, word count 3.3, scores 2.7.

Pydantic rejects bad input with 422 before it reaches a model: an unknown label, an empty or too long comment, a feature list of the wrong length, or a NaN. Every rejection is counted. Every prediction, online or batch, goes to `prediction_log` with the model version and the input's drift profile, not the comment text.

Jobs run inside the service on a timer, one at a time, each under a Postgres advisory lock:

| Job | Every | Does |
|---|---|---|
| `score_songs` | hour, or a minute while songs are waiting | Analyses up to 50 new or changed songs from Caligo's `mixtape` and `music` folders, one at a time, then groups the library with K-Means and finds the five nearest songs for each |
| `album_palettes` | 6 hours | K-Means palettes of the album covers |
| `moderate_backlog` | 10 minutes | Scores catalog comments that have no score from the current models. It reads `streamingservice.comment` in a read-only transaction |
| `drift_report` | day | PSI of the song features and predictions against each model's training data, and of the comment profiles and scores over 30 days |

The same jobs are Prefect flows in `caligo_ml/flows.py`. `docker compose --profile prefect up` starts a Prefect server with its UI on port 14200 and a flow runner with cron schedules and retries. Set `ML_SCHEDULE=off` then, so only Prefect schedules them. `caligo_ml/retraining.py` holds the `retrain` flow (Kaggle, then the gate), which runs where the Kaggle sign-in is.

Monitoring, from the service up:

1. Service health: `/health` and `/ready` with Docker health checks.
2. Input validity: rejected requests, silent, short or unreadable songs, features that are not finite.
3. Data drift: PSI per key feature between the training data and Caligo's own songs and comments.
4. Prediction drift: PSI of predicted genres, scores and moods.
5. Performance on labels: precision and recall of flags that admins confirmed or rejected, and genre accuracy on admin corrections.

## Quality gate

`tests/test_quality_gate.py` trains every candidate on small fixed samples of real data. A model past its floor fails the build.

Spam, trained on comments from four YouTube videos and scored on a fifth video it has never seen:

| Candidate | PR-AUC | F1 | Floor |
|---|---|---|---|
| logistic regression | 0.987 | 0.916 | 0.90 |
| Bernoulli naive Bayes | 0.978 | 0.914 | 0.90 |
| multinomial naive Bayes | 0.964 | 0.868 | 0.90 |
| RIPPER | 0.908 | 0.903 | 0.85 |
| decision tree | 0.905 | 0.882 | 0.85 |

Audio, on 1,400 FMA tracks (`tests/data/fma-sample`), using the frozen split:

| Task | Candidate | Score | Limit |
|---|---|---|---|
| genre, macro F1 (chance 0.125) | LightGBM | 0.519 | 0.44 |
| | RBF SVM | 0.500 | 0.42 |
| | logistic regression | 0.486 | 0.42 |
| | softmax regression | 0.484 | 0.42 |
| | two-layer net | 0.475 | 0.40 |
| | decision tree | 0.296 | 0.22 |
| mood, MAE | LightGBM | 0.142 | 0.18 |
| | two-layer net | 0.169 | 0.21 |
| | polynomial ridge regression | 0.198 | 0.24 |
| | gradient-descent linear regression | 0.206 | 0.25 |
| instrumental, PR-AUC (70% are instrumental) | RBF SVM | 0.894 | 0.83 |
| | polynomial logistic regression | 0.866 | 0.80 |
| | two-layer net | 0.833 | 0.78 |
| | logistic regression | 0.830 | 0.78 |
| | softmax regression | 0.814 | 0.77 |

## Demo activity

About 100 synthetic people with three months of activity, so the Thesaurarium isn't empty. Visitors see ordinary members. In the database every one of them has an `@demo.invalid` e-mail, and `unseed-demo` removes them all. They never get newsletter subscriptions, can't sign in (no password), and radio listener counts stay real.

| Part | Source | How |
|---|---|---|
| Who people are | Last.fm-1K profiles | Gender shares and the age distribution. Names come from fixed lists, Armenian and international names in separate pools |
| Films and ratings | MovieLens 32M, the 16 Caligo films found in it | A rank-3 `AlternatingLeastSquares` model fitted on 5,000 MovieLens users' ratings of those films. Each demo person gets a new taste (bias and factors) drawn from the fitted distribution, never a copied MovieLens user. Films are picked in proportion to how many MovieLens users rated them times the predicted liking. The rating is the prediction plus the model's residual noise, in half stars, doubled to Caligo's 0-10 scale. Films MovieLens doesn't have get the person's own average |
| When people listen | Last.fm-1K, 19.2M plays by 992 people | Local time of day (from the 549 people whose country has one time zone), day of week, how many days a person listens, sessions per day and session length (a new session starts after 30 minutes of silence) |
| What people listen to | Caligo's song analysis | Each person prefers one genre (the ML genre of each song), which orders the artists for them. Artists are picked with Zipf weights whose exponent is fitted so the favourite artist's share matches Last.fm's share within a listener's top 10 artists (Caligo has 10). The next track stays with the artist in album order with Last.fm's probability (48%), otherwise it follows "More like this" (CNN embeddings) half the time |
| Likes and comments | YouTube trending statistics, 199K videos | Each play is a chance to like the song at YouTube's median music like rate (4.0% of views). A like brings a comment as often as YouTube comments follow likes (8% for music, 16% for film). Films rated 9 or 10 are liked, and films rated 4 or lower can get a critical comment |
| Comment text | Written for this | Short templates that must fit the title (no "vocals" on an instrumental track, no "on repeat" after one play), each used once. All of them pass the spam and toxicity champions before they enter the plan |

Assumptions that aren't from data:

- a Last.fm listener does 5% of their listening on Caligo (the share of sessions kept);
- people watch half the films their taste picks;
- 30% start the series, and 85% go on to each next episode;
- 8% stop a film or episode partway;
- 35% of people with five favourites make a collection.

The generator compares itself with its sources and prints the result. The dev plan of 2026-11-07 gave session length median 10 tracks (Last.fm 10), favourite-artist share 21% (19%), a 0.86 correlation with Last.fm's listening hours, a 0.82 correlation with MovieLens per-film mean ratings, and a rating histogram distance of 0.12. Repeat plays are lower than Last.fm's (17% against 68%) because Last.fm covers years and the plan three months.

The steps (dev shown; prod uses `data/prod`):

```
# statistics on Kaggle (CPU): MovieLens 32M, Last.fm-1K, YouTube trending
.venv\Scripts\python -m caligo_ml kaggle demo_stats --root ..\..\data\dev\ml --films ..\..\data\dev\demo\catalog.json
# catalog export, inside the ML container (needs ML_DATABASE_URL and ML_CATALOG_DATABASE_URL)
python -m caligo_ml.demo.catalog --out /out/catalog.json
# the plan, with every comment checked by the moderation champions
.venv\Scripts\python -m caligo_ml.demo.generate --catalog ..\..\data\dev\demo\catalog.json --stats ..\..\data\dev\demo\demo_stats.json --models ..\..\data\dev\ml --out ..\..\data\dev\demo\plan.json
# seed and remove, with the API's own stores (writes seeded.json next to the plan)
dotnet Caligo.Api.dll seed-demo data/dev/demo/plan.json
dotnet Caligo.Api.dll unseed-demo data/dev/demo/seeded.json
```

`seed-demo` creates each account through Identity and `StreamingAccountService` (Free plan, dated the day the person joined), then replays every watch, rating, comment and collection in time order through the catalog stores, so the schema's triggers fill in recommendations, milestones and average ratings as they would for real members. Watches, sign-ups and collections keep their dates. Ratings and comments have no date column, so they get only the order. `unseed-demo` deletes everything those accounts own. It then puts back each rating average the seeding changed: the value from before seeding if nobody rated the title since, otherwise the average of the remaining ratings.

## Talking points

**In one breath.** The service listens to every song in Caligo's library and says what it sounds like (genre, mood, instrumental or vocal). It also finds songs that sound alike, picks album accent colours and moderates comments. The models are trained on public datasets on Kaggle, logged to MLflow, and promoted only when they beat the current champion on a frozen test split. A FastAPI service serves them with scikit-learn and ONNX Runtime, logs every prediction, and checks drift against the training data and week over week.

**Numbers to remember.**

- Genre over 8 FMA genres (chance 0.125): LightGBM on audio features, macro-F1 0.597. The ResNet18 CNN on log-mel spectrograms, fine-tuned on a Kaggle GPU: 0.677.
- Mood: LightGBM MAE 0.118 (R² 0.61) over energy, valence and danceability.
- Moderation: spam with logistic regression, PR-AUC 0.983. Toxicity with DistilBERT quantised to int8 (67 MB): PR-AUC 0.928, against 0.878 for logistic regression.
- The gate kept a spam challenger out because it only tied the champion on the same test rows.
- Demo tastes: ALS fills in hidden ratings at RMSE 0.25 against 1.51 for the mean (on the test matrix). The simulated listening matches Last.fm's median session of 10 tracks.

**What real data taught.**

- Public data doesn't carry over cleanly. Caligo's commercial masters are far louder than FMA's tracks (loudness PSI 8.28), so drift flags the inputs even when predictions look right. Retraining on the same data can't fix that, which is why admin genre labels are the real accuracy check.
- The best model on FMA wasn't the best on Caligo. The RBF SVM won instrumental on FMA (PR-AUC 0.930) but called 87% of Caligo's songs instrumental. On 289 songs labelled by album, softmax regression scored 0.803 against the SVM's 0.625, so it was promoted by hand with the reason stored on the model version.
- YouTube spam doesn't look like film comments. The score alone gave 10 false flags on dev, so a spam flag now also needs a RIPPER rule that fired, and the admin queue shows that rule next to the comment.

**The MLOps parts.**

- Tracking and registry: one MLflow experiment per task, champion and challenger aliases, and the champion exported with its drift reference.
- Serving: FastAPI with Pydantic validation and a 422 handler that records what was rejected. FastAPI's default handler echoed NaN back and crashed.
- Monitoring in five layers: container health and OpenTelemetry, rejected inputs, input drift (PSI), prediction drift (against training and week over week), and accuracy on admin labels.
- CI: ruff, pytest with scikit-learn's estimator checks on the from-scratch models, a quality gate on a fixed sample, then an image build and a container smoke test.
- Training: each Kaggle kernel carries the package. Tabular tasks run on CPU, the CNN and DistilBERT on a GPU, and prod never talks to Kaggle.

## Run (from this folder)

```
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\ruff check .
.venv\Scripts\python -m pytest
```

## Data and licences

- FMA metadata and features: CC BY 4.0. A sample is in `tests/data/fma-sample/` with its source note.
- YouTube Spam Collection (UCI): CC BY 4.0. In `tests/data/youtube-spam/` with its source note.
- Jigsaw Toxic Comment Classification: Wikipedia talk page comments, released under CC0 with the comment text under Wikipedia's CC BY-SA 3.0. It is downloaded through the Kaggle competition and not stored here.
- MovieLens 32M (GroupLens): research use, no redistribution. The demo uses aggregate statistics and a model fitted on it; no MovieLens user's ratings are copied into Caligo.
- Last.fm-1K (MTG, Universitat Pompeu Fabra): non-commercial research use. Only aggregate statistics are kept.
- Trending YouTube Video Statistics (Kaggle `datasnaek/youtube-new`): CC0-1.0. Only like and comment rates are kept.
- The statistics and plans live in `data/<env>/demo/`, outside git.
