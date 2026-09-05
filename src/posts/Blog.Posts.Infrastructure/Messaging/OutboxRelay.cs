using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Messaging
{
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

            _logger.LogInformation("Outbox relay started, polling every {Interval}.", _options.OutboxPollInterval);

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
                    _logger.LogError(ex, "Outbox dispatch pass failed; retrying.");
                }

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

            return await context.OutboxMessages
                .Where(m => m.ProcessedAt == null && m.Attempts < _options.MaxDeliveryAttempts)
                .OrderBy(m => m.OccurredAt)
                .Take(_options.OutboxBatchSize)
                .ToListAsync(cancellationToken);
        }
    }
}
