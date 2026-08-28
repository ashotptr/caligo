using Microsoft.Extensions.Options;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

/// <summary>
/// Re-enqueues deliveries that were recorded but never reached the broker.
///
/// The relay writes a Pending row, then publishes. If the process dies in between - or RabbitMQ
/// is unreachable at that moment - the row exists and no command does, so an email is owed and
/// nothing is going to send it. Kafka does not help here: either its offset was committed, and
/// the event is gone, or it was not, and the deduplication table now suppresses the retry.
///
/// A full outbox with its own relay would close this properly, as the posts service does for
/// its lifecycle events. This is the cheaper answer for a lower-stakes payload: the Pending row
/// is already the record that something is owed, so recovering it is a query rather than a
/// second table and a second background loop. Worth naming as the trade-off it is.
/// </summary>
public class StrandedDeliverySweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ICommandQueue _queue;
    private readonly NotificationsOptions _options;
    private readonly ILogger<StrandedDeliverySweeper> _logger;

    public StrandedDeliverySweeper(
        IServiceScopeFactory scopes,
        ICommandQueue queue,
        IOptions<NotificationsOptions> options,
        ILogger<StrandedDeliverySweeper> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RabbitConfigured)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sweep failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();

        var ledger = scope.ServiceProvider.GetRequiredService<IDeliveryLedger>();

        var stranded = await ledger.StrandedAsync(_options.StrandedAfter, 100, cancellationToken);

        if (stranded.Count == 0)
        {
            return;
        }

        var requeued = new List<Guid>();

        foreach (var delivery in stranded)
        {
            var subscriber = await ledger.SubscriberAsync(delivery.SubscriberId, cancellationToken);

            if (subscriber is null || !subscriber.IsActive)
            {
                // They unsubscribed while this sat here. Sending anyway would be worse than
                // dropping it, so it is closed out rather than delivered late.
                await ledger.MarkFailedAsync(
                    delivery.Id, delivery.Attempts, "Subscriber is no longer active.", cancellationToken);

                continue;
            }

            await _queue.EnqueueAsync(new SendEmailCommand(
                delivery.Id,
                delivery.EventId,
                subscriber.Id,
                subscriber.Email,
                subscriber.DisplayName,
                delivery.PostId,
                delivery.PostTitle,
                subscriber.UnsubscribeToken), cancellationToken);

            requeued.Add(delivery.Id);
        }

        await ledger.MarkQueuedAsync(requeued, cancellationToken);

        if (requeued.Count > 0)
        {
            _logger.LogWarning("Swept {Count} stranded delivery/deliveries back onto the queue.", requeued.Count);
        }
    }
}
