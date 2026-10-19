using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Caligo.Notifications.Core;

namespace Caligo.Notifications.Host.Persistence;

public class EfSubscriberStore : ISubscriberStore
{
    private readonly NotificationsDbContext _context;

    public EfSubscriberStore(NotificationsDbContext context)
    {
        _context = context;
    }

    public async Task<Subscriber> SubscribeAsync(
        string email, string? displayName, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        var rows = await _context.Subscribers
            .FromSql($"""
                INSERT INTO subscribers
                    ("Id", "Email", "DisplayName", "IsActive", "SubscribedAt", "UnsubscribedAt", "UnsubscribeToken")
                VALUES ({Guid.NewGuid()}, {normalized}, {displayName}, true, now(), NULL, {NewToken()})
                ON CONFLICT ("Email") DO UPDATE
                    SET "IsActive" = true,
                        "UnsubscribedAt" = NULL,
                        "DisplayName" = COALESCE(EXCLUDED."DisplayName", subscribers."DisplayName")
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows[0];
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

    public EfDeliveryLedger(NotificationsDbContext context)
    {
        _context = context;
    }

    public async Task<FanOutChunk> ClaimChunkAsync(
        Guid eventId, int postId, string postTitle, Guid after, int chunkSize,
        CancellationToken cancellationToken = default)
    {
        var chunk = await _context.Subscribers
            .FromSql($"""
                SELECT * FROM subscribers
                WHERE "IsActive" AND "Id" > {after}
                ORDER BY "Id"
                LIMIT {chunkSize}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (chunk.Count == 0)
        {
            return new FanOutChunk(Array.Empty<SendEmailCommand>(), null);
        }

        var subscriberIds = chunk.Select(s => s.Id).ToArray();

        var claimed = await _context.Database
            .SqlQuery<ClaimedRow>($"""
                INSERT INTO notification_deliveries
                    ("Id", "EventId", "SubscriberId", "PostId", "PostTitle", "Status", "Attempts", "CreatedAt")
                SELECT gen_random_uuid(), {eventId}, subscriber_id, {postId}, {postTitle},
                       {nameof(DeliveryStatus.Pending)}, 0, now()
                FROM unnest({subscriberIds}) AS subscriber_id
                ON CONFLICT ("EventId", "SubscriberId") DO NOTHING
                RETURNING "Id", "SubscriberId"
                """)
            .ToListAsync(cancellationToken);

        var bySubscriber = chunk.ToDictionary(s => s.Id);

        var commands = claimed
            .Select(row =>
            {
                var subscriber = bySubscriber[row.SubscriberId];

                return new SendEmailCommand(
                    row.Id,
                    eventId,
                    subscriber.Id,
                    subscriber.Email,
                    subscriber.DisplayName,
                    postId,
                    postTitle,
                    subscriber.UnsubscribeToken);
            })
            .ToList();

        return new FanOutChunk(commands, chunk.Count == chunkSize ? chunk[^1].Id : null);
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

        await Open(deliveryId)
            .ExecuteUpdateAsync(
                d => d.SetProperty(x => x.Status, DeliveryStatus.Sent)
                      .SetProperty(x => x.SentAt, now)
                      .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default)
    {
        await Open(deliveryId)
            .ExecuteUpdateAsync(
                d => d.SetProperty(x => x.Status, DeliveryStatus.Failed)
                      .SetProperty(x => x.Attempts, attempts)
                      .SetProperty(x => x.LastError, Truncate(error)),
                cancellationToken);
    }

    public async Task RecordAttemptAsync(
        Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default)
    {
        await Open(deliveryId)
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

    public async Task<bool> RunExclusiveAsync(
        string lockName, Func<Task> work, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var acquired = await _context.Database
            .SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock(hashtext({lockName})) AS "Value" """)
            .ToListAsync(cancellationToken);

        if (!acquired[0])
        {
            return false;
        }

        await work();

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    private IQueryable<NotificationDelivery> Open(Guid deliveryId) =>
        _context.Deliveries.Where(d =>
            d.Id == deliveryId &&
            (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Queued));

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000];

    internal sealed class ClaimedRow
    {
        public Guid Id { get; set; }

        public Guid SubscriberId { get; set; }
    }
}
