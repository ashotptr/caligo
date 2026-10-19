CREATE TABLE IF NOT EXISTS radio_plays
(
    started_at DateTime64(3, 'UTC'),
    file       String,
    artist     String,
    album      String,
    title      String,
    is_request Bool
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(started_at)
ORDER BY started_at
TTL toDateTime(started_at) + INTERVAL 2 YEAR;

CREATE TABLE IF NOT EXISTS radio_listeners
(
    sampled_at DateTime('UTC'),
    source     LowCardinality(String),
    listeners  UInt32
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(sampled_at)
ORDER BY (source, sampled_at)
TTL sampled_at + INTERVAL 1 YEAR;
