# 004 - Transactional outbox for post lifecycle events

**Status:** accepted

## Context

Publishing a post has to do two things: commit a row to PostgreSQL and emit an event to Kafka.
There is no transaction spanning both.

Publishing inside the request gives a choice of two failure modes, and both are real:

- publish first, and a rolled-back write leaves an event announcing a post that does not exist;
- commit first, and a crash before the publish leaves a post nobody was ever told about.

Retrying at the edge does not help, because the process that would retry is the one that died.

## Decision

Write the event as a row in the same transaction as the post. A background relay claims unsent
rows with `FOR UPDATE SKIP LOCKED`, publishes them, and marks them sent.

Views deliberately do **not** go through the outbox - see below.

## Consequences

**Delivery becomes at-least-once, not exactly-once.** A crash after Kafka acknowledges but
before the row is marked republishes it. That is inherent, not a gap: every consumer
deduplicates on `EventId` rather than pretending otherwise.

**`SKIP LOCKED`, not plain `FOR UPDATE`.** The latter would serialise every API replica behind
whichever one got there first. With `SKIP LOCKED` each takes a different set of rows.

**A partial index** (`WHERE "ProcessedAt" IS NULL`) keeps the relay's query proportional to the
backlog rather than to the history of every event ever sent.

**Guarantees are chosen per event class.** `post.viewed` goes straight to Kafka,
fire-and-forget. A view is a measurement: nothing downstream is authoritative on it, and making
every page render write a PostgreSQL row to protect a counter is the wrong trade. `post.published`
goes through the outbox because losing it means a post nobody was told about. Applying one
mechanism uniformly would have been easier to explain and worse.

**A side benefit.** The row stores the W3C `traceparent` captured during the request, so a
publish minutes later rejoins the trace that caused it rather than starting an orphan.
