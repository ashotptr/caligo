using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Messaging
{
    /// <summary>
    /// Relays outbox rows to Kafka.
    ///
    /// This is the half of the transactional outbox that runs outside the request. It claims a
    /// batch of unsent rows, publishes them, and marks them sent - retrying indefinitely until
    /// the broker accepts them, which is what turns "the database committed" into "the event
    /// will eventually be delivered".
    ///
    /// Delivery is at-least-once and cannot be made exactly-once here: if the process dies
    /// after Kafka acknowledges but before the row is marked, the row is claimed again and
    /// republished. Consumers deduplicate on EventId rather than pretending otherwise.
    /// </summary>
    public class OutboxRelay : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IKafkaProducer _producer;
        private readonly KafkaOptions _options;
        private readonly ILogger<OutboxRelay> _logger;

        public OutboxRelay(
            IServiceScopeFactory scopes,
            IKafkaProducer producer,
            IOptions<KafkaOptions> options,
            ILogger<OutboxRelay> logger)
        {
            _scopes = scopes;
            _producer = producer;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("No Kafka broker configured; outbox relay not started.");

                return;
            }

            _logger.LogInformation(
                "Outbox relay started, polling every {Interval}.", _options.OutboxPollInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                var dispatched = 0;

                try
                {
                    dispatched = await DispatchBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The relay must outlive any single failure; the rows are still in the
                    // table and will be picked up on the next pass.
                    _logger.LogError(ex, "Outbox dispatch pass failed; retrying.");
                }

                // A full batch probably means more is waiting, so go straight round again.
                if (dispatched < _options.OutboxBatchSize)
                {
                    try
                    {
                        await Task.Delay(_options.OutboxPollInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("Outbox relay stopped.");
        }

        private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopes.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var batch = await ClaimBatchAsync(context, cancellationToken);

            if (batch.Count == 0)
            {
                return 0;
            }

            foreach (var message in batch)
            {
                try
                {
                    await _producer.ProduceAsync(
                        new KafkaMessage(
                            message.Topic,
                            message.PartitionKey,
                            message.Payload,
                            message.EventType,
                            message.SchemaVersion,
                            message.TraceParent),
                        cancellationToken);

                    message.ProcessedAt = DateTime.UtcNow;
                    message.LastError = null;
                }
                catch (Exception ex)
                {
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                    if (message.Attempts >= _options.MaxDeliveryAttempts)
                    {
                        // Give up on this one so it stops blocking the batch behind it. The row
                        // stays unprocessed and visible, which is the point: it is a queue of
                        // things a human needs to look at, not a silent drop.
                        _logger.LogError(
                            ex,
                            "Outbox message {MessageId} ({EventType}) failed {Attempts} times and will no longer be retried.",
                            message.Id, message.EventType, message.Attempts);
                    }
                    else
                    {
                        _logger.LogWarning(
                            ex,
                            "Outbox message {MessageId} failed (attempt {Attempts}); will retry.",
                            message.Id, message.Attempts);
                    }
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var sent = batch.Count(m => m.ProcessedAt is not null);

            if (sent > 0)
            {
                _logger.LogInformation("Relayed {Sent} outbox message(s) to Kafka.", sent);
            }

            return batch.Count;
        }

        /// <summary>
        /// Claims a batch for this instance.
        ///
        /// On PostgreSQL the rows are locked with FOR UPDATE SKIP LOCKED, so several API
        /// instances can run a relay each: every instance takes a different set of rows instead
        /// of blocking on one another or publishing the same event twice. SKIP LOCKED rather
        /// than plain FOR UPDATE is the important part - the latter would serialise the
        /// instances behind whichever one got there first.
        /// </summary>
        private async Task<List<OutboxMessage>> ClaimBatchAsync(
            ApplicationDbContext context, CancellationToken cancellationToken)
        {
            if (context.Database.IsNpgsql())
            {
                return await context.OutboxMessages
                    .FromSqlRaw(
                        """
                        SELECT * FROM outbox_messages
                        WHERE "ProcessedAt" IS NULL AND "Attempts" < {0}
                        ORDER BY "OccurredAt"
                        LIMIT {1}
                        FOR UPDATE SKIP LOCKED
                        """,
                        _options.MaxDeliveryAttempts,
                        _options.OutboxBatchSize)
                    .ToListAsync(cancellationToken);
            }

            // SQLite has no row-level locking and only ever runs single-process here, so the
            // ordinary query is both correct and sufficient.
            return await context.OutboxMessages
                .Where(m => m.ProcessedAt == null && m.Attempts < _options.MaxDeliveryAttempts)
                .OrderBy(m => m.OccurredAt)
                .Take(_options.OutboxBatchSize)
                .ToListAsync(cancellationToken);
        }
    }
}
