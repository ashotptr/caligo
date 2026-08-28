# 007 - No SQL Server

**Status:** accepted

## Context

SQL Server is the default relational database in a lot of .NET work, and is often named
alongside PostgreSQL as an expected skill.

## Decision

PostgreSQL for relational data, MongoDB for the view stream, Redis for caching and the trending
leaderboard. No SQL Server.

## Consequences

Each store here was chosen for something specific, and adding a fourth would mean one of them
had nothing to do:

- **PostgreSQL** - posts, identity, the outbox, the delivery ledger. Used for things SQL Server
  would not do better and several it does differently: `tsvector` with a GIN index for search,
  `FOR UPDATE SKIP LOCKED` for the outbox relay, `jsonb`, `text[]` with containment.
- **MongoDB** - individual view records. Unbounded, append-only, never updated, queried by
  aggregation. A relational schema would add constraints nothing needs.
- **Redis** - cache-aside for posts, and sorted sets for the trending rollup, which is a data
  structure rather than a cache.

Adding SQL Server would mean a fourth datastore, a fourth thing to operate, and a fourth
connection string, in exchange for demonstrating familiarity with a product that solves no
problem this system has.

The EF Core work is provider-agnostic in the places that matter, and the search strategy is
already split per provider (PostgreSQL full-text, SQLite `LIKE` for tests), so adding a provider
later is a class, not a rewrite.
