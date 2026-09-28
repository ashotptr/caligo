-- Raw events. ReplacingMergeTree folds rows with the same sorting key (event_id is part of it)
-- during merges, so a replay after a crash converges to one row per event.
--
-- Retries are handled at insert time instead. Every batch is inserted with an
-- insert_deduplication_token, and non_replicated_deduplication_window makes this non-replicated
-- table remember recent tokens, so a batch written twice is stored once. The deduplicated block is
-- still pushed through the materialized views, so each rollup table keeps its own window too; there
-- the token is derived from the batch's token, which drops the repeat but never two different
-- batches that happen to aggregate to the same rows.
CREATE TABLE IF NOT EXISTS post_views
(
    event_id      UUID,
    post_id       Int32,
    viewed_at     DateTime64(3, 'UTC'),
    visitor_hash  String,
    user_id       String,
    referrer_host LowCardinality(String)
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(viewed_at)
ORDER BY (post_id, viewed_at, event_id)
TTL toDateTime(viewed_at) + INTERVAL 180 DAY
SETTINGS non_replicated_deduplication_window = 1000;

-- Per-post, per-minute rollup, filled at insert time. Readers merge a few thousand rows instead of
-- scanning raw events; uniq keeps an approximate distinct count that can be merged across minutes.
CREATE TABLE IF NOT EXISTS post_views_1m
(
    post_id  Int32,
    minute   DateTime('UTC'),
    views    SimpleAggregateFunction(sum, UInt64),
    visitors AggregateFunction(uniq, String)
)
ENGINE = AggregatingMergeTree
PARTITION BY toYYYYMM(minute)
ORDER BY (post_id, minute)
TTL minute + INTERVAL 2 YEAR
SETTINGS non_replicated_deduplication_window = 1000;

ALTER TABLE post_views_1m MODIFY SETTING non_replicated_deduplication_window = 1000;

CREATE MATERIALIZED VIEW IF NOT EXISTS post_views_1m_mv TO post_views_1m AS
SELECT
    post_id,
    toStartOfMinute(viewed_at) AS minute,
    count() AS views,
    uniqState(visitor_hash) AS visitors
FROM post_views
GROUP BY post_id, minute;

CREATE TABLE IF NOT EXISTS clap_events
(
    event_id   UUID,
    post_id    Int32,
    user_id    String,
    delta      Int32,
    first_clap Bool,
    clapped_at DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(clapped_at)
ORDER BY (post_id, clapped_at, event_id)
TTL toDateTime(clapped_at) + INTERVAL 180 DAY
SETTINGS non_replicated_deduplication_window = 1000;

CREATE TABLE IF NOT EXISTS claps_1m
(
    post_id  Int32,
    minute   DateTime('UTC'),
    claps    SimpleAggregateFunction(sum, Int64),
    clappers AggregateFunction(uniq, String)
)
ENGINE = AggregatingMergeTree
PARTITION BY toYYYYMM(minute)
ORDER BY (post_id, minute)
TTL minute + INTERVAL 2 YEAR
SETTINGS non_replicated_deduplication_window = 1000;

ALTER TABLE claps_1m MODIFY SETTING non_replicated_deduplication_window = 1000;

CREATE MATERIALIZED VIEW IF NOT EXISTS claps_1m_mv TO claps_1m AS
SELECT
    post_id,
    toStartOfMinute(clapped_at) AS minute,
    toInt64(sum(delta)) AS claps,
    uniqState(user_id) AS clappers
FROM clap_events
GROUP BY post_id, minute;
