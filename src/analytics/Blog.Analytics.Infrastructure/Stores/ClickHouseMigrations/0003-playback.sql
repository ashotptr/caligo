CREATE TABLE IF NOT EXISTS playback_events
(
    event_id    UUID,
    device_id   String,
    seq         UInt64,
    session_id  UUID,
    content_id  Int32,
    kind        LowCardinality(String),
    delivery    LowCardinality(String),
    occurred_at DateTime64(3, 'UTC'),
    position    Float32,
    duration_ms UInt32,
    from_height UInt16,
    to_height   UInt16,
    detail      String
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (content_id, session_id, device_id, seq)
TTL toDateTime(occurred_at) + INTERVAL 180 DAY
SETTINGS non_replicated_deduplication_window = 1000;
