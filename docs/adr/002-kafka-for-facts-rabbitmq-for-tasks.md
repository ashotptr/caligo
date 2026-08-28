# 002 - Kafka for facts, RabbitMQ for tasks

**Status:** accepted

## Context

Running two message brokers looks like indecision. It is worth being precise about why one
would not do.

Post views started on RabbitMQ. That stopped fitting the moment a second reader was wanted: a
rolling "trending" leaderboard, alongside the existing raw-view writer. A queue delivers each
message to exactly one consumer, so the two projections would each have seen roughly half the
traffic. Duplicating the message into a second queue would work, but it still could not answer
the other question - "add a new metric next month and backfill it from history" - because a
consumed message is gone.

Email is the opposite problem. Each outbound message is addressed to one person, fails on its
own, needs retry with backoff, and must be given up on and parked somewhere a human can look.
Kafka has no per-message acknowledgement, no per-message delay, and no dead-letter queue.
Worse, its best feature is actively dangerous here: replaying a topic would re-send every
announcement to every subscriber.

## Decision

**Kafka carries facts.** `post.viewed` and `post.lifecycle` are append-only, replayable, and
read by independent consumer groups at their own offsets.

**RabbitMQ carries tasks.** Per-subscriber email commands, with a TTL-based retry ladder
(5s / 30s / 5m) and a dead-letter queue.

`Blog.Notifications` translates between them: it consumes one Kafka fact and produces N RabbitMQ
commands. That translation is exactly what neither broker does for you.

## Consequences

**Cost.** Two brokers to run, monitor and understand. Anyone joining has to learn both.

**Benefit.** Each side gets semantics it would otherwise have to reimplement. The retry ladder
is per-queue message TTL plus dead-lettering - real exponential backoff with no plugin and no
timer in the worker. Replay is a consumer-group offset reset.

**The asymmetry that follows.** Replaying `post.viewed` is harmless; the projections recount.
Replaying `post.lifecycle` would re-mail everyone, so the notifications side deduplicates on
`(event_id, subscriber_id)` with a unique index. The same stream needs a different guarantee on
each side, and that is a property of what the consumer *does*, not of the stream.

Verified: rewinding the relay group to offset 0 and replaying the whole topic produced 8 emails
before and 8 after.
