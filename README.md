# Blog

This project is a distributed blog platform. It is split into three ASP.NET Core
services that own separate databases and talk over Kafka, RabbitMQ and gRPC. The
frontend is React 19 + TypeScript, served by nginx.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/architecture-dark.svg">
  <img alt="Architecture: blog-client to Blog.Api, gRPC to Blog.Analytics, Kafka to both consumers, RabbitMQ to SMTP" src="docs/architecture-light.svg" width="660">
</picture>

`Blog.Api` owns posts, claps, scheduling, identity, PostgreSQL and Redis.

`Blog.Analytics` owns view counts, trending, MongoDB, Redis and ClickHouse.

`Blog.Notifications` owns subscribers, the delivery ledger and its own
PostgreSQL database.

## How it works

**Kafka.** Three topics:

```
blog.post.viewed.v1      6 partitions, 7 day retention
blog.post.clapped.v1     6 partitions, 7 day retention
blog.post.lifecycle.v1   3 partitions, 30 day retention
```

The producer runs with `acks=all` and idempotence on. Every message carries
`event-type`, `schema-version` and `traceparent` headers. Five consumer groups
read those topics. `analytics-raw-writer`, `analytics-trending` and
`analytics-olap` all read `blog.post.viewed.v1` at their own offsets, so each one
receives every message. `analytics-olap-claps` reads `blog.post.clapped.v1` and
`notifications-relay` reads `blog.post.lifecycle.v1`.

A consumer commits a batch only after storing it. A batch that fails is retried
as the same batch, with its partitions paused so nothing piles up behind it. It
is dropped only if its partitions move to another consumer, which reads those
messages again. The notifications relay does the same one event at a time: an
event whose fan-out fails is read again rather than skipped, and a failed commit
is logged rather than stopping the service.

**The outbox.** No transaction spans PostgreSQL and Kafka, so publishing a post
writes the event as a row in the same transaction as the post. A relay polls
every 2 seconds and claims up to 100 rows. It hands the whole batch to the
producer before awaiting any send, then marks the rows sent. A row that fails 10
times stops being retried and stays in the table.

`post.viewed` and `post.clapped` skip the outbox and go straight to the
producer. PostgreSQL holds the authoritative clap counts, so the stream only
feeds analytics.

**Concurrency.** Every flow that can race uses the cheapest tool that makes it
correct:

| Flow | Technique | Why |
|---|---|---|
| Clapping (max 50 per reader per post) | One `INSERT … ON CONFLICT DO UPDATE` at READ COMMITTED | `SET` reads the row as it was before the update, so the delta applied after the cap is exact even when one reader's requests race. There is no read-then-write. |
| A post's clap total | Claps are appended to `clap_deltas`, and a worker folds them in batches with `FOR UPDATE SKIP LOCKED` | A viral post's total would be one hot row. Batching turns thousands of updates into one per post, and replicas fold disjoint batches in parallel. Totals are upserted in post-id order, so two folders cannot deadlock. |
| Clap audit | REPEATABLE READ READ ONLY | Its three queries must see one snapshot. At READ COMMITTED, a fold committing between them looks like a mismatch. |
| Scheduled-post quota | SERIALIZABLE, retried on `40001` | "Count, then insert" is write skew at READ COMMITTED and REPEATABLE READ. Scheduling is rare, so retries cost nothing. |
| Publishing due posts | `FOR UPDATE SKIP LOCKED` on a partial index, status flip and outbox row in one transaction | Every API replica publishes, and each post is claimed and announced exactly once. A burst due at the same second is split between replicas. |
| Post edits | `Version` concurrency token, `ETag` and `If-Match` | A stale edit gets 412 and a lost race gets 409, instead of silently overwriting. |
| Refresh-token rotation | Compare-and-set `UPDATE … WHERE ReplacedBySessionId IS NULL` | Two refreshes with one token: exactly one wins, and the other is treated as a replay. |
| Newsletter fan-out | Chunked `INSERT … SELECT … ON CONFLICT DO NOTHING` | Claims a large audience in bounded pieces without loading it into memory. Replaying the event creates nothing and drops nothing. |
| Stranded-delivery sweep | `pg_try_advisory_xact_lock` | One replica sweeps at a time. The lock ends with the transaction, so it cannot leak through the connection pool. |
| Delivery status | Conditional updates out of Pending and Queued only | A late message cannot turn Sent back into Failed. |

The public feed pages with a row comparison, `("PublishedDate", "Id") < (@date, @id)`,
which the btree index takes as its start key: a page 40,000 posts deep scans
its 21 rows. The equivalent `OR` is only a filter there, and the same page
scanned more than 10,000 rows first. The plan tests measure both.

`tests/Blog.IntegrationTests/Concurrency` holds the evidence:

- **Isolation lab.** Drives two connections step by step and shows what each level allows on PostgreSQL:
  - no dirty reads, even at READ UNCOMMITTED
  - non-repeatable reads and phantoms at READ COMMITTED, but not at REPEATABLE READ
  - lost updates at READ COMMITTED, and `40001` at REPEATABLE READ
  - write skew at REPEATABLE READ, and `40001` at SERIALIZABLE
  - a torn multi-query read
  - a deadlock, and the lock ordering that avoids it

  A blocked step is detected through `pg_stat_activity`, not a sleep.
- **Plan tests.** Seed about 60,000 rows with `COPY` and assert through `EXPLAIN` that each hot query uses the index built for it.
- **Race tests.** Run the real code with hundreds of parallel callers.

**Caching.** Post pages and details are cached in Redis. A miss runs the query
once per key, whoever else is waiting for it (single-flight). Every key embeds a
generation: one per post, one for the list pages. A write bumps the generation
with `INCR`, so an edit expires only that post, and a read that loaded before the
edit writes its stale copy under a generation nobody reads any more. TTLs carry
±10% jitter so entries written together do not expire together.

**ClickHouse.** `analytics-olap` and `analytics-olap-claps` write views and claps
in batches, each batch one `INSERT` carrying an `insert_deduplication_token` built
from its topic, partition and offsets. A write that timed out but landed, then
retried, is stored once. Raw events go to ReplacingMergeTree tables. Materialized
views keep per-minute AggregatingMergeTree rollups: views, claps, and `uniq`
visitors and clappers. The rollups keep their own deduplication window, because
ClickHouse still pushes a deduplicated block through its views. Reads come from
the rollups:

```
GET /api/analytics/posts/{id}/series?from&to&bucket=minute|hour|day
GET /api/analytics/top/window?minutes=15&by=views|claps     Admin, Writer
GET /api/analytics/posts/{id}/funnel?days=7                 Admin, Writer
```

The funnel lines up views, unique visitors, clappers and claps for one post,
across both streams.

**RabbitMQ.** Three exchanges and five queues:

```
blog.notifications         direct   - blog.notifications.email
blog.notifications.retry   direct   - retry.5s, retry.30s, retry.5m
blog.notifications.dlq     fanout   - blog.notifications.dlq
```

The retry queues have no consumer. Each carries an `x-message-ttl` of 5000,
30000 or 300000 ms and dead-letters back to the main exchange when it expires, so
a failed message reappears on the email queue after the delay. After 4 attempts
it is routed to the fanout.

Delivery is at-least-once. A unique index on `EventId` in MongoDB and on
`(event_id, subscriber_id)` in the ledger absorbs the duplicates. Replaying the
whole lifecycle topic produced 8 emails before and 8 after.

**gRPC.** Seven RPCs on port 8080, bound as HTTP/2. Port 8081 is HTTP/1 for the
health probes. `StreamTrending` is a server stream that the API bridges to
Server-Sent Events because a browser cannot consume gRPC. The client gives
lookups a 150 ms deadline and the three ClickHouse queries 2 s.

## Features

- Transactional outbox, so a committed post is always published
- Scheduled publishing, exactly once across replicas, with a SERIALIZABLE per-author quota
- Claps capped per reader in one atomic statement, folded into totals in batches
- Optimistic concurrency with `ETag` and `If-Match` on post edits
- Isolation-level lab, query-plan tests and race tests against real PostgreSQL
- Cache with single-flight misses, per-post generations and TTL jitter
- Real-time OLAP in ClickHouse: rollups, minute-precision windows, an engagement funnel
- Consumers that retry a failed batch in place and dedupe retried inserts
- Five Kafka consumer groups on three topics, reading at independent offsets
- RabbitMQ retry ladder at 5s, 30s and 5m, then a dead letter queue
- gRPC unary, batch and server streaming, bridged to SSE for the browser
- Tracing with the `traceparent` stored on the outbox row
- Full-text search on a GIN index with weighted ranking, and keyset paging
- NBomber load tests for view spikes, clap storms, fan-out, publish bursts and hot rows

## Configuration

`appsettings.json` holds the defaults. Compose overrides
the values. An empty
value disables the thing it configures instead of failing the boot:

```
ConnectionStrings:Redis        Empty disables caching and trending.
ConnectionStrings:ClickHouse   Empty disables the OLAP consumers and endpoints.
Kafka:BootstrapServers         Empty stops the relay and all consumer groups.
Analytics:Address              http://host:port of the analytics gRPC service.
Otlp:Endpoint                  Empty disables telemetry export.
Database:Provider              Postgres or Sqlite.
Jwt:Key                        At least 32 bytes, checked at startup.
Smtp:Host                      Empty disables sending. MailHog locally, a real relay in production.
```

Tuning, with the defaults:

```
Posts:MaxScheduledPerAuthor          25
Posts:SchedulerInterval              00:00:01   Zero stops the background publisher.
Claps:RollupInterval                 00:00:01
Claps:RollupBatchSize                5000
Notifications:FanOutChunkSize        5000
RateLimiting:ReadsPermitLimit        120 per ReadsWindow (1 minute), per client address
ForwardedHeaders:KnownNetworks       Proxy networks whose X-Forwarded-For is trusted
```

Mail is only sent on `post.published`, and only to addresses on the subscriber
list, which is separate from the account list and joined through the subscribe
form.

## Tests

123 tests run without Docker. The 55 integration tests start real PostgreSQL,
Kafka, MongoDB, Redis and ClickHouse with Testcontainers and need it.

## Load tests

`tests/Blog.LoadTests` drives the running stack with NBomber. Every scenario
reports latency and throughput, then checks its invariants against the databases
and exits non-zero if one fails.

```bash
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.loadtest.yml \
  up -d --build --scale blog-api=3
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.loadtest.yml \
  run --rm load-tests all    # or one scenario: run --rm load-tests clap-storm --rate 200
```

The scenarios run in a container on the compose network, so they measure the
stack rather than the host's port forwarding. `dotnet run -c Release --project
tests/Blog.LoadTests -- all` runs them from the host instead, against
`127.0.0.1`: on Windows `localhost` resolves to `::1` first, and WSL's relay can
accept those connections without ever forwarding them.

The override lifts the per-client rate limit and the scheduling quota, loads
`pg_stat_statements` and traces 5% of requests instead of all of them. It also
frees the API's host port so three replicas sit behind nginx.

| Scenario | Load | Checks |
|---|---|---|
| `views-spike` | A new post read at `--rate` req/s (100) from a cold cache | Postgres detail reads per request (single-flight); every view reaches ClickHouse once, and how fast |
| `clap-storm` | `--readers` readers (500) clapping one post at `--rate` req/s (100) | Some readers reach 50 and nobody passes it; total equals the per-reader sum; audit clean; rollup backlog and drain time |
| `fanout-spike` | A post published to `--subscribers` subscribers (100,000) | Time to claim and queue every delivery; replaying the event adds none |
| `scheduled-burst` | `--posts` posts due at the same second (2,000) | Time to publish all and deliver their events; one `post.published` each |
| `hot-row` | `--copies` writers (64) on one counter row | Throughput, p99 and retries for seven strategies; which ones lose updates |

Reports and a Markdown summary per run go to `tests/Blog.LoadTests/reports`.
NBomber is free for personal use; organisations need a licence.

### Results

These numbers are a floor. They were measured on a laptop: an Intel i7-1355U (10
cores, 12 threads, 15 W) with 16 GB of RAM, and Docker Desktop given 12 CPUs and
8 GB. The stack (13 to 15 containers), the load generator, an IDE and a browser
all shared it, the CPU ran below its base clock and the Docker VM was swapping.
Repeated runs of one scenario varied by as much as 5×. Every invariant held in
every run below.

**hot-row**: 64 writers adding 1 to one row for 15 s. Median of three runs, with
the range:

| Strategy | Increments/s | p99 | Retries per increment | Updates lost |
|---|---|---|---|---|
| Naive read-modify-write | 136 (87–176) | 2.3 s | 0 | 98–99% |
| Atomic `UPDATE … SET value = value + 1` | 229 (75–262) | 4.4 s | 0 | none |
| `SELECT … FOR UPDATE`, then `UPDATE` | 161 (142–179) | 2.1 s | 0 | none |
| Version check, retry on conflict | 43 (14–46) | 10.0 s | 9.7 | none |
| SERIALIZABLE, retry on `40001` | 64 (23–117) | 6.4 s | 17.6 | none |
| Sharded counter, 16 rows | 935 (819–1,920) | 399 ms | 0 | none |
| Append a delta, fold in batches (claps) | 2,742 (964–5,265) | 65 ms | 0 | none |

Only the naive version lost updates. With 64 writers on one row nearly every
optimistic or serializable attempt conflicts, so they were the slowest. Taking
the write off the row is what scales: appending deltas and folding them in
batches, as claps do, ran 12× faster than the atomic `UPDATE`.

**The other scenarios**, one run each in the same session, after a warm-up. Each
run started with the notifications pipeline idle and a fresh PostgreSQL
checkpoint:

| Scenario | Load | 1 API replica | 3 API replicas |
|---|---|---|---|
| `views-spike` | 3,200 reads of a new post at 100/s | p50 7.7 ms, p99 293 ms | p50 391 ms, p99 9.1 s |
| `clap-storm` | 3,000 clap requests at 100/s from 500 readers | p50 16 ms, p99 178 ms | p50 117 ms, p99 1.5 s |
| `scheduled-burst` | 2,000 posts due in the same second | all live in 1.4 s | all live in 10.9 s |

- **views-spike:** no errors. One or two post reads reached PostgreSQL for the
  3,200 requests. Every view could be queried in ClickHouse 1 to 1.5 s after the
  load stopped.
- **clap-storm:** 69 and 75 readers reached the 50-clap cap and none passed it.
  The post total equalled the per-reader sum, the audit was clean, and
  ClickHouse's sum matched PostgreSQL's.
- **scheduled-burst:** each post was announced exactly once.
- **fanout-spike:** a post published to 100,001 subscribers had every delivery
  claimed and queued in 65.8 s, about 1,500 a second. Replaying the event added
  no deliveries.

Three replicas on one laptop were slower, not faster. They add no CPU, only more
runtimes, background workers and connection pools competing for the same cores,
and most likely more of the swapping. What the three-replica runs do show is
that the coordination holds:
- three publishers split each burst, and none announced a post twice;
- three rollup workers folded one backlog without double counting.

The first runs found two bugs, both fixed:
- In Development, every request probed PostgreSQL before building its
  `DbContext`. A probe that timed out under load quietly put that one request
  on the SQLite fallback. The provider is now chosen once per process.
- A transient Kafka commit error stopped the notifications service. A post whose
  fan-out failed was skipped once the next post's offset was committed, so it
  was never announced.

## Usage

### Dependencies

.NET 8 SDK, Node 22, Docker and Docker Compose. The brokers and databases all
come from the compose stack.

### Run

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --build
```

This will build the four images, start PostgreSQL, Redis, MongoDB, Kafka,
RabbitMQ and ClickHouse, create the Kafka topics, apply the migrations and seed
an admin user.

- Site: <http://localhost:8080>, sign in `admin@localhost.dev` / `AdminDev04!`
- Traces, metrics and logs: <http://localhost:18888>
- Kafka topics and consumer lag: <http://localhost:8081>
- RabbitMQ, the retry ladder and the DLQ: <http://localhost:15672>, `blog` /
  `blogpass`
- ClickHouse SQL console: <http://localhost:8123/play>, `blog` / `blogpass`
- Every email the stack sends, captured by MailHog: <http://localhost:8025>
- Health: `:5028` api, `:5029` analytics, `:5030` notifications, `/health/ready`

## CI

Builds and tests everything, then rebuilds only the images whose service changed.
