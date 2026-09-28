using Microsoft.Extensions.Options;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

public class StrandedDeliverySweeper : BackgroundService
{
    public const string SweepLock = "notifications:stranded-sweep";

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

        // Every replica runs this loop. Without the lock each one would read the same stranded rows
        // and enqueue them, and every subscriber would get one email per replica.
        var swept = await ledger.RunExclusiveAsync(
            SweepLock, () => RequeueStrandedAsync(ledger, cancellationToken), cancellationToken);

        if (!swept)
        {
            _logger.LogDebug("Another replica is sweeping; skipping this round.");
        }
    }

    private async Task RequeueStrandedAsync(IDeliveryLedger ledger, CancellationToken cancellationToken)
    {
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
                await ledger.MarkFailedAsync(
                    delivery.Id, delivery.Attempts, "Subscriber is no longer active.", cancellationToken);

                continue;
            }

            var published = await _queue.EnqueueAsync(new SendEmailCommand(
                delivery.Id,
                delivery.EventId,
                subscriber.Id,
                subscriber.Email,
                subscriber.DisplayName,
                delivery.PostId,
                delivery.PostTitle,
                subscriber.UnsubscribeToken), cancellationToken);

            if (published)
            {
                requeued.Add(delivery.Id);
            }
        }

        await ledger.MarkQueuedAsync(requeued, cancellationToken);

        if (requeued.Count > 0)
        {
            _logger.LogWarning("Swept {Count} stranded delivery/deliveries back onto the queue.", requeued.Count);
        }
    }
}
