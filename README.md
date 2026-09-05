# Blog

This project is a distributed blog platform. Instead of one application it is
split into three ASP.NET Core services that own separate databases and talk to
each other over Kafka, RabbitMQ and gRPC. The frontend is React 19 + TypeScript,
served by nginx.

The main idea of the project is that no service reaches into another service's
database. Anything crossing a service boundary is either an event on a broker or
a gRPC call, and each path is chosen for what it has to guarantee.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/architecture-dark.svg">
  <img alt="Architecture: blog-client to Blog.Api, gRPC to Blog.Analytics, Kafka to both consumers, RabbitMQ to SMTP" src="docs/architecture-light.svg" width="660">
</picture>

## The three services

**Blog.Api:** posts and identity. Owns PostgreSQL and Redis. Produces to Kafka
and calls analytics over gRPC.

**Blog.Analytics:** view counts and trending. Owns MongoDB and Redis. Runs two
Kafka consumer groups and a gRPC server.

**Blog.Notifications:** subscribers and the delivery ledger. Owns its own
PostgreSQL database. Kafka in, RabbitMQ out, SMTP at the end.

## src/posts/

Four projects with dependencies pointing inwards.

`Blog.Posts.Domain/`: entities, value objects and domain events. References
nothing but the BCL. `BlogPost.cs` raises events and hands them over with
`DrainDomainEvents()`, so reading the list also clears it.

`Blog.Posts.Application/`: use cases and ports. No EF Core, no ASP.NET.
`PostService.cs` holds the rules, `Abstractions/` holds the interfaces that
infrastructure implements.

`Blog.Posts.Infrastructure/`: everything with a driver in it.

- `Persistence/EfPostRepository.cs`: saves the post and dispatches its events in
  one transaction.
- `Persistence/OutboxDomainEventDispatcher.cs`: turns domain events into outbox
  rows on the same `DbContext`. Stores the current `traceparent` on the row.
- `Persistence/LoggingDomainEventDispatcher.cs`: the same port with no broker
  configured. Logs and returns.
- `Messaging/OutboxRelay.cs`: a background loop that claims unsent rows with
  `FOR UPDATE SKIP LOCKED` and publishes them.
- `Messaging/KafkaProducer.cs`: the producer, idempotent with `acks=all`.
- `Messaging/GrpcViewStatsClient.cs`: the analytics client, with a deadline, one
  retry and a circuit breaker.
- `Search/PostgresPostSearch.cs` and `Search/SqlitePostSearch.cs`: two backends
  behind one interface, because SQLite has no `tsvector`.
- `Caching/DistributedPostCache.cs`: Redis. Keys carry a version number, and a
  write increments it, which invalidates every key of the previous generation
  at once.

`Blog.Api/`: controllers and DI wiring only. `TokenService.cs` issues the JWTs,
`RefreshSessionService.cs` handles rotation.

## src/analytics/

`Messaging/ViewConsumer.cs`: the Kafka consumers. Two groups read the same
`post.viewed` topic at their own offsets, so both get every message.

`Stores/MongoViewStore.cs`: writes every view into the `post_views` collection.
Two indexes: a compound one on `(ViewedAt, PostId)` for the time-window queries,
and a unique one on `EventId`. On a replayed event the insert raises error 11000
and the store catches it.

`Stores/RedisTrendingStore.cs`: the leaderboard. One sorted set per hour, keyed
`trending:2026090514`, incremented with `ZINCRBY` and expired after 49 hours.
Reads union the last N buckets with `ZUNIONSTORE`.

`Services/AnalyticsReaderService.cs`: the gRPC server. Implements the four RPCs
declared in `analytics.proto`.

## src/notifications/

`Messaging/PostLifecycleRelay.cs`: consumes `blog.post.lifecycle.v1`, filters on
the `event-type` header, and publishes one RabbitMQ command per subscriber. It
commits the Kafka offset only after the commands are queued.

`Persistence/EfStores.cs`: the delivery ledger. `ClaimAsync` inserts one Pending
row per subscriber. A unique index on `(event_id, subscriber_id)` resolves the
case where two instances claim the same event at once: the second insert fails
and that instance returns an empty list.

`Messaging/RabbitTopology.cs`: the exchanges and queues.
`blog.notifications` → `blog.notifications.email`, with retry queues at 5s, 30s
and 5m and a fanout to `blog.notifications.dlq` at the end.

`Messaging/EmailWorker.cs`: consumes commands and sends through SMTP.

`Messaging/StrandedDeliverySweeper.cs`: finds deliveries left in Queued by a
process that stopped mid-flight and returns them to Pending.

## src/shared/

`Blog.Contracts/`: the integration events, `Topics.cs` and the header names.
References nothing outside the BCL, so all three services depend on it.

`Blog.Contracts.Grpc/`: the generated gRPC contract. `Grpc.Tools` runs protoc at
build time with `GrpcServices="Both"`, producing `AnalyticsReaderBase` for the
server and `AnalyticsReaderClient` for the caller from the one `.proto`.

`Blog.Observability/`:

- `Telemetry.cs`: traces, metrics and logs over OTLP. If `Otlp:Endpoint` is
  empty the method returns early and no exporter is registered.
- `KafkaTracing.cs`: writes the W3C `traceparent` into Kafka headers and reads it
  back on the other side.
- `HealthEndpoints.cs`: `/health/live` and `/health/ready`.

## Messaging

**Kafka.** Two topics, declared in `Blog.Contracts/Topics.cs` and created at boot
by `KafkaTopicProvisioner.cs`:

```
blog.post.viewed.v1      6 partitions, 7 day retention
blog.post.lifecycle.v1   3 partitions, 30 day retention
```

`KafkaProducer.cs` publishes with `acks=all`, idempotence enabled, 10 retries at
200 ms, 5 ms linger and Snappy compression. Every message carries three headers:
`event-type`, `schema-version` and `traceparent`.

Three consumer groups read those topics. `analytics-raw-writer` and
`analytics-trending` both read `blog.post.viewed.v1` at their own offsets, so
each one receives every message. `notifications-relay` reads
`blog.post.lifecycle.v1`.

**The outbox.** No transaction spans PostgreSQL and Kafka, so the event is
written as a row instead. `OutboxDomainEventDispatcher.cs` adds it to the same
`DbContext` as the post, and one `SaveChangesAsync` commits both or neither.
`OutboxRelay.cs` polls every 2 seconds, claims up to 100 rows with
`FOR UPDATE SKIP LOCKED`, publishes them and marks them sent. A row that fails 10
times stops being retried and stays in the table.

SKIP LOCKED is what lets several API instances each run a relay. Every instance
claims a different set of rows instead of blocking on whichever one got there
first.

`post.viewed` does not use the outbox. `KafkaAnalyticsPublisher.cs` hands it to
the producer's background send thread and returns without waiting for the result.

**RabbitMQ.** `RabbitTopology.cs` declares three exchanges and five queues:

```
blog.notifications         direct   → blog.notifications.email
blog.notifications.retry   direct   → retry.5s, retry.30s, retry.5m
blog.notifications.dlq     fanout   → blog.notifications.dlq
```

The retry queues have no consumer. Each carries an `x-message-ttl` of 5000,
30000 or 300000 ms and dead-letters back to the main exchange when the TTL
expires, so a failed message reappears on the email queue after the delay. After
4 attempts it is routed to the fanout instead.

**gRPC.** `analytics.proto` declares four RPCs. `Blog.Analytics.Host/Program.cs`
binds port 8080 as HTTP/2 for them and port 8081 as HTTP/1 for health probes.
Traffic between pods is cleartext, and Kestrel cannot negotiate the protocol
without TLS, so the split is per port rather than per request.

`GetPostStats`, `GetPostStatsBatch` and `GetTopPosts` are unary. `StreamTrending`
is a server stream, which the API bridges to Server-Sent Events because a browser
cannot consume gRPC.

`GrpcViewStatsClient.cs` wraps all four with a 150 ms deadline, one retry on a
transient `RpcException`, and a circuit breaker that opens at a 0.5 failure ratio
over 8 calls in 15 seconds. Every failure path returns null, which the controller
renders as no counts.

Delivery is at-least-once. `MongoViewStore.cs` carries a unique index on
`EventId` and catches the duplicate-key error, `EfStores.cs` carries one on
`(event_id, subscriber_id)`. Replaying the whole lifecycle topic produced 8
emails before and 8 after.

Implemented features: posts with tags and full-text search, keyset paging, JWT
auth with refresh session rotation, Google sign-in, Admin and Writer roles, view
counts, a live trending panel over SSE, email subscriptions with unsubscribe
tokens, per-subscriber retry and dead lettering, and distributed tracing across
every hop.

Not done yet: there is no alerting on the telemetry, and the refresh token is
kept in `localStorage` rather than an HttpOnly cookie.

## Failure behaviour

The gRPC client carries a 150 ms deadline, one retry and a circuit breaker, and
every failure path returns no counts rather than propagating the error.

Measured with the analytics service stopped, calls cost about 350 ms while the
breaker samples, then 24 ms once it opens. Posts keep rendering throughout.

`/health/live` checks nothing external. A liveness probe that touched the
database would turn a database blip into a cluster-wide restart storm.
`/health/ready` checks dependencies, so failing it drains an instance without
killing it.

## Performance

Measured on 200k seeded posts against PostgreSQL 16:

- Search for a term with no matches: 3,960 ms sequential scan → 0.1 ms on the
  GIN index.
- Search for a term matching 25k rows: 2 ms unranked → 3 ms relevance ranked.
- Paging to row 150,000: 14.6 ms with `OFFSET` → 0.12 ms with a keyset cursor.

The first attempt at ranking was slower than the scan it replaced, at 89 ms on
broad terms. The GIN lookup was only 3 ms of that. Ranking was reading 22k heap
blocks to score every match. Bounding it to a 500-row candidate window made it
flat regardless of how many rows matched.

## Configuration

`appsettings.json` per service. Empty values disable the thing they configure
rather than failing the boot:

```
ConnectionStrings:Postgres   the posts database
ConnectionStrings:Redis      host:6379. Empty disables caching and trending.
Kafka:BootstrapServers       host:9092. Empty stops the relay and both consumers.
Analytics:Address            http://host:port of the analytics gRPC service.
Analytics:Mongo              mongodb://... Empty disables durable view storage.
Otlp:Endpoint                Empty disables all telemetry export.
Database:Provider            Postgres or Sqlite.
Jwt:Key                      Must be at least 32 bytes, checked at startup.
```

## files inside tests

`Blog.Posts.UnitTests/`: domain and application tests, plus
`ArchitectureTests.cs`, which reads the assembly references and fails if the
domain pulls in anything but the BCL or the application layer pulls in EF Core,
Npgsql, MongoDB, RabbitMQ, Redis or ASP.NET.

`Blog.Notifications.UnitTests/`: the retry ladder, with a fake `TimeProvider` so
the backoff does not actually wait.

`Blog.Api.Tests/`: the API over HTTP against in-memory SQLite.

`Blog.IntegrationTests/`: Testcontainers. Starts real PostgreSQL, Kafka, MongoDB
and Redis, so the outbox and the consumers are tested against the actual brokers.

74 tests run without Docker. The integration suite needs it.

## Usage

### Dependencies

.NET 8 SDK, Node 22, Docker and Docker Compose. Nothing else is needed, the
brokers and databases all come from the compose stack.

### Build the project

```bash
dotnet build
```

### Run

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --build
```

This will:

1. Build all four images.
2. Start PostgreSQL, Redis, MongoDB, Kafka and RabbitMQ.
3. Create the Kafka topics on first boot.
4. Apply the migrations and seed an admin user.

After boot:

- Site: <http://localhost:8080>, sign in with `admin@localhost.dev` /
  `AdminDev04!`
- Traces, metrics and logs: <http://localhost:18888>
- Kafka topics and consumer lag: <http://localhost:8081>
- RabbitMQ, the retry ladder and the DLQ: <http://localhost:15672>, `blog` /
  `blogpass`
- Every email the stack sends: <http://localhost:8025>
- Health: `:5028` api, `:5029` analytics, `:5030` notifications, on
  `/health/ready`

The trending stream, directly:

```bash
curl -N http://localhost:8080/api/analytics/trending/stream
```

### Develop

```bash
dotnet test
cd src/blog-client && npm ci && npm run dev
```

The API falls back to SQLite when PostgreSQL is configured but unreachable, so
`dotnet run --project src/posts/Blog.Api` works with nothing else running.

### Migrations

Always scaffolded against PostgreSQL. SQLite builds its schema from the model
instead.

```bash
dotnet ef migrations add <Name> \
  --project src/posts/Blog.Posts.Infrastructure \
  --startup-project src/posts/Blog.Api
```

### Kubernetes

```bash
kubectl apply -k deploy/k8s/overlays/dev
```

Kustomize rather than Helm. No templating language to debug, and it is built into
`kubectl`.

The `dev` overlay runs the dependencies in-cluster, so an empty cluster comes up
working. The `prod` overlay leaves them out, expects managed services, and
contains no Secret at all. `blog-secrets` is created out of band by External
Secrets, Sealed Secrets or a cloud CSI driver.

Autoscaling differs by workload. The API scales on CPU. The consumers scale on
Kafka consumer lag and RabbitMQ queue depth through KEDA, because CPU is the
wrong signal for a consumer. A million-event backlog looks like an idle pod when
the bottleneck is MongoDB.

`deploy/k8s/validate.py` checks what `kustomize build` cannot: probes pointing at
undeclared ports, Services selecting nothing, autoscalers targeting a renamed
Deployment, a `ScaledObject` fighting a static replica count. It runs in CI.

Note: the `dev` overlay drops every deployment to one replica. The base keeps 3.
It is for seeing the thing run, not for load.

## CI

Builds and tests everything, then rebuilds only the images whose service changed.
A change to shared contracts rebuilds all of them, a change to one service's own
code does not. Images are scanned with Trivy and the results are uploaded to the
security tab.
