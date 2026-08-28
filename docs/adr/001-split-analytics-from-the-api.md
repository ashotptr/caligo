# 001 - Split analytics out of the API

**Status:** accepted

## Context

The blog started as one ASP.NET Core process doing four unrelated jobs: identity, post CRUD,
caching, and a RabbitMQ consumer writing page views into MongoDB. The consumer ran as a
`BackgroundService` in the same process as the web host.

Two things made that uncomfortable rather than merely untidy:

- **Opposite shapes.** Authoring is write-light, relational and transactional. View telemetry is
  write-heavy, unbounded, append-only and never updated. They wanted different stores and
  different scaling.
- **Shared fate.** They shared a thread pool, a connection pool and a deployment. A burst of
  traffic meant the view consumer competed with request handling for the same threads, and
  neither could be scaled without the other.

## Decision

Extract a `Blog.Analytics` service owning view counts and trending, with MongoDB and Redis of
its own. The API keeps posts and identity.

## Consequences

**Cost.** A join that used to be one LINQ query is now a network call: analytics returns counts
keyed by post id and does not know what a title is, so the API joins titles from its own
database. Two processes to deploy, and a boundary that has to be versioned.

**Benefit.** Each side scales on its own signal - the API on CPU, the consumers on Kafka lag.
An analytics outage degrades to "no view counts" instead of taking the blog down, which is
enforced by a deadline, a circuit breaker and a null-returning client rather than by hope.

**What would make this wrong.** If analytics were only ever read on one admin page, this would
be two deployments for no benefit. It earns its place because every post render asks for a
count, and because the write path is genuinely high volume.
