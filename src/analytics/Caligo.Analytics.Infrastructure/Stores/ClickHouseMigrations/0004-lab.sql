CREATE TABLE IF NOT EXISTS lab_runs
(
    run_id         UUID,
    scenario       LowCardinality(String),
    profile        String,
    started_at     DateTime64(3, 'UTC'),
    finished_at    DateTime64(3, 'UTC'),
    listeners      UInt16,
    seconds        UInt16,
    repetitions    UInt8,
    contestants    UInt16,
    source_bias_ms Nullable(Float32),
    notes          String
)
ENGINE = ReplacingMergeTree(finished_at)
ORDER BY run_id
TTL toDateTime(started_at) + INTERVAL 2 YEAR;

CREATE TABLE IF NOT EXISTS lab_windows
(
    run_id              UUID,
    contestant          LowCardinality(String),
    server              LowCardinality(String),
    protocol            LowCardinality(String),
    container           LowCardinality(String),
    repetition          UInt8,
    started_at          DateTime64(3, 'UTC'),
    finished_at         DateTime64(3, 'UTC'),
    impaired_packets    UInt64,
    impaired_lost       UInt64,
    impaired_overflowed UInt64
)
ENGINE = MergeTree
ORDER BY (run_id, contestant, repetition)
TTL toDateTime(started_at) + INTERVAL 2 YEAR;

CREATE TABLE IF NOT EXISTS lab_results
(
    run_id          UUID,
    contestant      LowCardinality(String),
    repetition      UInt8,
    listener        UInt16,
    probe           Bool,
    recorded_at     DateTime64(3, 'UTC'),
    connected       Bool,
    ttfb_ms         Nullable(Float32),
    startup_ms      Nullable(Float32),
    stalls          UInt16,
    stall_ms        Float32,
    media_ms        Float32,
    bytes           UInt64,
    latency_ms      Nullable(Float32),
    data_latency_ms Nullable(Float32),
    error           String
)
ENGINE = MergeTree
ORDER BY (run_id, contestant, repetition, listener)
TTL toDateTime(recorded_at) + INTERVAL 2 YEAR;

CREATE TABLE IF NOT EXISTS lab_latency
(
    run_id          UUID,
    contestant      LowCardinality(String),
    repetition      UInt8,
    measured_at     DateTime64(3, 'UTC'),
    latency_ms      Float32,
    data_latency_ms Nullable(Float32)
)
ENGINE = MergeTree
ORDER BY (run_id, contestant, measured_at)
TTL toDateTime(measured_at) + INTERVAL 2 YEAR;

CREATE TABLE IF NOT EXISTS lab_resources
(
    run_id       UUID,
    container    LowCardinality(String),
    sampled_at   DateTime64(3, 'UTC'),
    cpu_percent  Float32,
    memory_bytes UInt64
)
ENGINE = MergeTree
ORDER BY (run_id, container, sampled_at)
TTL toDateTime(sampled_at) + INTERVAL 2 YEAR;
