# 006 - One test framework

**Status:** accepted

## Context

xUnit, NUnit and Moq are all commonly listed together as .NET testing skills. The project
already used xUnit.

## Decision

xUnit everywhere, plus Moq where there are interfaces worth faking, plus Testcontainers where
the question can only be answered by real infrastructure. No NUnit.

## Consequences

**Why not add NUnit anyway.** It would demonstrate nothing except a willingness to add a
dependency. Two frameworks in one solution means two runners, two attribute vocabularies and
two sets of conventions, for tests that are otherwise identical. "I know it, I picked one on
purpose" is a better answer than a project split for appearances.

**Where each tool earns its place.**

- **Moq** became useful only after the layering in ADR-001 produced real ports. Mocking an EF
  `DbContext` proves nothing; mocking `IPostRepository` to assert that a cache hit never reaches
  the database proves something specific.
- **Testcontainers** answers questions a fake cannot. "Do these two writes share a transaction"
  is a claim about PostgreSQL. "Does a replayed event produce a duplicate" is a claim about a
  unique index. An in-memory provider has no transaction semantics to violate.
- **Architecture tests** enforce the dependency rule by reflection, because layering described
  only in a README erodes the first time someone reaches for a `DbContext` in the wrong place.
