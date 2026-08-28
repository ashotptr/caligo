# Architecture Decision Records

Short notes on decisions that were not obvious, written when they were made rather than
reconstructed afterwards. Each says what was decided, what it cost, and what would make it
wrong later.

The ones about what was *not* built are as deliberate as the rest.

| # | Decision |
|---|---|
| [001](001-split-analytics-from-the-api.md) | Split analytics out of the API |
| [002](002-kafka-for-facts-rabbitmq-for-tasks.md) | Kafka for facts, RabbitMQ for tasks |
| [003](003-grpc-for-internal-reads.md) | gRPC for internal reads |
| [004](004-transactional-outbox.md) | Transactional outbox for post lifecycle events |
| [005](005-no-api-gateway.md) | No API gateway |
| [006](006-one-test-framework.md) | One test framework |
| [007](007-no-sql-server.md) | No SQL Server |
| [008](008-hand-written-health-checks.md) | Hand-written health checks |
