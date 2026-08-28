using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Persistence;

public class EfSubscriberStore : ISubscriberStore
{
    private readonly NotificationsDbContext _context;

    public EfSubscriberStore(NotificationsDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Subscriber>> ActiveAsync(CancellationToken cancellationToken = default) =>
        await _context.Subscribers
            .AsNoTracking()
            .Where(s => s.IsActive)
            .ToListAsync(cancellationToken);

    public async Task<Subscriber> SubscribeAsync(
        string email, string? displayName, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        var existing = await _context.Subscribers.FirstOrDefaultAsync(s => s.Email == normalized, cancellationToken);

        if (existing is not null)
        {
            // Re-subscribing reactivates rather than creating a second row, so the unique index
            // on the address is never in the way of someone changing their mind.
            existing.IsActive = true;
            existing.UnsubscribedAt = null;
            existing.DisplayName = displayName ?? existing.DisplayName;

            await _context.SaveChangesAsync(cancellationToken);

            return existing;
        }

        var subscriber = new Subscriber
        {
            Email = normalized,
            DisplayName = displayName,
            UnsubscribeToken = NewToken()
        };

        _context.Subscribers.Add(subscriber);

        await _context.SaveChangesAsync(cancellationToken);

        return subscriber;
    }

    public async Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default)
    {
        var subscriber = await _context.Subscribers
            .FirstOrDefaultAsync(s => s.UnsubscribeToken == token, cancellationToken);

        if (subscriber is null)
        {
            return false;
        }

        subscriber.IsActive = false;
        subscriber.UnsubscribedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public class EfDeliveryLedger : IDeliveryLedger
{
    private readonly NotificationsDbContext _context;
    private readonly ILogger<EfDeliveryLedger> _logger;

    public EfDeliveryLedger(NotificationsDbContext context, ILogger<EfDeliveryLedger> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ClaimAsync(
        Guid eventId, int postId, string postTitle,
        IReadOnlyList<Subscriber> subscribers,
        CancellationToken cancellationToken = default)
    {
        if (subscribers.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        // Which of these have already been claimed for this event, by this or another instance.
        var already = await _context.Deliveries
            .AsNoTracking()
            .Where(d => d.EventId == eventId)
            .Select(d => d.SubscriberId)
            .ToListAsync(cancellationToken);

        var seen = already.ToHashSet();

        var fresh = subscribers
            .Where(s => !seen.Contains(s.Id))
            .Select(s => new NotificationDelivery
            {
                EventId = eventId,
                SubscriberId = s.Id,
                PostId = postId,
                PostTitle = postTitle,
                Status = DeliveryStatus.Pending
            })
            .ToList();

        if (fresh.Count == 0)
        {
            return Array.Empty<NotificationDelivery>();
        }

        _context.Deliveries.AddRange(fresh);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two relay instances claimed the same event at once. The read above narrows the
            // window but cannot close it; the unique index is what actually decides, and losing
            // the race simply means the other instance is sending these.
            _logger.LogInformation("Event {EventId} was claimed concurrently; yielding.", eventId);

            foreach (var entry in _context.ChangeTracker.Entries<NotificationDelivery>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            return Array.Empty<NotificationDelivery>();
        }

        return fresh;
    }

    public async Task MarkQueuedAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken cancellationToken = default)
    {
        if (deliveryIds.Count == 0)
        {
            return;
        }

        await _context.Deliveries
            .Where(d => deliveryIds.Contains(d.Id) && d.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Status, DeliveryStatus.Queued), cancellationToken);
    }

    public async Task MarkSentAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await _context.Deliveries
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(
                d => d.SetProperty(x => x.Status, DeliveryStatus.Sent)
                      .SetProperty(x => x.SentAt, now)
                      .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default)
    {
        await _context.Deliveries
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(
                d => d.SetProperty(x => x.Status, DeliveryStatus.Failed)
                      .SetProperty(x => x.Attempts, attempts)
                      .SetProperty(x => x.LastError, Truncate(error)),
                cancellationToken);
    }

    public async Task RecordAttemptAsync(
        Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default)
    {
        await _context.Deliveries
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(
                d => d.SetProperty(x => x.Attempts, attempts)
                      .SetProperty(x => x.LastError, Truncate(error)),
                cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> StrandedAsync(
        TimeSpan olderThan, int limit, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        return await _context.Deliveries
            .AsNoTracking()
            .Where(d => d.Status == DeliveryStatus.Pending && d.CreatedAt < cutoff)
            .OrderBy(d => d.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<Subscriber?> SubscriberAsync(Guid subscriberId, CancellationToken cancellationToken = default) =>
        await _context.Subscribers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriberId, cancellationToken);

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000];

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || exception.InnerException?.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) == true;
}
