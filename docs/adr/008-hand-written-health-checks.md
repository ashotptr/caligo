# 008 - Hand-written health checks

**Status:** accepted

## Context

`AspNetCore.Diagnostics.HealthChecks` provides checks for PostgreSQL, Redis, Kafka, RabbitMQ and
MongoDB. The PostgreSQL and Redis ones were used and work. Three did not.

## Decision

Keep the packaged PostgreSQL and Redis checks. Write the Kafka, RabbitMQ and MongoDB ones by
hand - roughly thirty lines each, using clients the services already hold.

## Consequences

Each replacement had its own reason, and none of them was "not invented here":

- **Kafka.** The packaged check works by *producing a message* to a probe topic. Auto-create is
  disabled here on purpose, so the topic does not exist and the check failed for a reason
  unrelated to health. Even with the topic present, a readiness probe that writes to the system
  it is checking - every ten seconds, in every replica - is a side effect nobody asked for. A
  metadata request answers the same question and changes nothing.
- **MongoDB.** The 8.x package is built against driver 2.x, whose separate `MongoDB.Driver.Core`
  assembly no longer exists in 3.x. It failed at runtime with a missing-assembly error: a health
  check reporting the database down because of its own dependency, which is worse than no check.
- **RabbitMQ.** Its options-based overload silently accepted a lambda that set nothing, then
  threw during startup - taking the service down on boot rather than reporting unhealthy.

**Cost.** Roughly ninety lines to own, and the packaged checks' future improvements are foregone.

**Benefit.** Three fewer dependencies, three fewer version-compatibility surfaces, and three
checks that do exactly what a readiness probe should: ask a question without changing anything.

## A related decision: what liveness checks

`/health/live` deliberately checks nothing external. A liveness probe that touched PostgreSQL
would fail during a database blip, the kubelet would restart every replica, and a recoverable
outage would become an outage plus a restart storm. `/health/ready` checks dependencies -
failing it removes an instance from the load balancer without killing it, so it rejoins on its
own.
