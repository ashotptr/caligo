# **StreamingServiceSystem**
This project is the complete **relational database** behind a video streaming service, built on **MySQL**. The schema, the sample data, and the analytics all live here: recommendations, notifications, and rating aggregation are implemented with stored functions and triggers.

### `create_insert.sql`
A full MySQL script that rebuilds the entire system from scratch: it creates the `streamingservice` database, defines all **24 tables** with their foreign keys and indexes, loads the sample data, and creates the views.

* **`content` is the supertype:** every watchable item, a movie or a single episode, is a row in `content` (title, description, duration, release date, production company, average rating).
* **Movies and series:** `movie` extends `content` with budget and box-office figures, while series are modeled as `series` → `season` → `episode`, where every episode points to its own `content` row.
* **Many-to-many junctions:** `contentactor`, `contentgenres`, `contentlanguages`, `contentdistributingcompanies`, and `subtitle` connect titles to their actors, genres, languages, distributors, and available subtitles.
* **Users and money:** `user` (unique email, subscription window), `subscriptionplan`, and `payments`.
* **Activity:** `watchhistory`, `usercontentinteraction` (likes and ratings), `comment`, `notifications`, and `currentcontentrecommendations` record everything users do and everything the database decides to tell them.
* Foreign keys enforce integrity everywhere; watch history, interactions, and payments are cascade-deleted together with their owner.

### `view_create.sql`
Three analytical views over user activity:

* **`popularcontent`:** the 10 most-watched titles by total views.
* **`activeusers`:** the 10 most active users, ranked by views, interactions, and comments combined.
* **`engagingcontent`:** the 10 titles with the highest engagement ratio, interactions and comments per view.

### `function_create.sql`
Three stored functions used by the queries and the triggers:

* **`calculatesimilarity(a, b)`:** the **Jaccard index** of two titles over their genre sets, shared genres divided by all genres between them.
* **`checkactivesubscription(user)`:** whether today falls inside the user's subscription window.
* **`calculatealltimespending(user)`:** everything a user has ever paid.

### `triggers_create.sql`
Three triggers turn inserts into extended behavior:

* **`after_watch_completion`:** every time a row lands in `watchhistory`, the user's old recommendations are deleted and the 10 titles most similar to what they just watched (by Jaccard genre similarity) are inserted into `currentcontentrecommendations`. The recommendation engine is a trigger.
* **`after_user_watch_count_update`:** sends a notification when a user crosses 50, 100, or 200 distinct watched titles.
* **`update_content_rating_after_feedback`:** recomputes a title's `average_rating` the moment a new rating arrives.

### `queries.sql`
Ten queries: recent watch activity, the top watcher in every genre, content ranked by interactions, the top spenders, the 10 titles most similar to the current most popular one, users who watch but never engage, the actors behind the 3 most profitable movies, and what expired subscribers watched while their subscription was still active.

### `indexes_create.sql`
The indexes behind the queries.

### `normalization.txt`
Functional-dependency analysis: the candidate keys of the relations are listed, and each is shown to be in **3NF** and to satisfy **BCNF**.

### `manual_insert/insert.sql`
Seed data. It populates every table except `notifications` and `currentcontentrecommendations`, the two tables the database fills in by itself.

### `StreamingServiceERD.pdf` / `StreamingServiceRS.pdf`
The design documents: the entity-relationship diagram and the relational schema the database was built from.

---

## Usage
### Set up the database

```
mysql -u root -p < create_insert.sql

```

This will:

1. Create the `streamingservice` database.
2. Create all 24 tables with their foreign keys and indexes.
3. Load the sample data.
4. Create the three views.

### Load the functions and triggers

```
mysql -u root -p streamingservice < function_create.sql
mysql -u root -p streamingservice < triggers_create.sql

```

### Run the analytics

```
mysql -u root -p streamingservice -t < queries.sql

```

### Watch the database react
From a `mysql` session on the `streamingservice` database, simulate user 21 finishing Fight Club:

```
INSERT INTO watchhistory (content_id, user_id, date) VALUES (3, 21, NOW());
SELECT * FROM currentcontentrecommendations WHERE user_id = 21;

```

The trigger fires on the insert and immediately rebuilds the user's top-10 recommendations by genre similarity.