namespace Blog.Notifications.Core;

public interface ISubscriberStore
{
    Task<Subscriber> SubscribeAsync(string email, string? displayName, CancellationToken cancellationToken = default);

    Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default);
}

// One chunk of a newsletter fan-out: the deliveries this call created (subscribers that already
// have one for the event are skipped) and the subscriber id the next chunk starts after, or null
// when this was the last chunk.
public record FanOutChunk(IReadOnlyList<SendEmailCommand> Claimed, Guid? Next);

public interface IDeliveryLedger
{
    Task<FanOutChunk> ClaimChunkAsync(
        Guid eventId, int postId, string postTitle, Guid after, int chunkSize,
        CancellationToken cancellationToken = default);

    Task MarkQueuedAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken cancellationToken = default);

    Task MarkSentAsync(Guid deliveryId, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default);

    Task RecordAttemptAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> StrandedAsync(
        TimeSpan olderThan, int limit, CancellationToken cancellationToken = default);

    Task<Subscriber?> SubscriberAsync(Guid subscriberId, CancellationToken cancellationToken = default);

    // Runs the work while holding a lock that every replica sees. Returns false without running it
    // when another replica holds the lock.
    Task<bool> RunExclusiveAsync(string lockName, Func<Task> work, CancellationToken cancellationToken = default);
}

public interface ICommandQueue
{
    bool IsConfigured { get; }

    // Returns false when the command could not be handed to the broker; the delivery then stays
    // Pending and the sweeper retries it.
    Task<bool> EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default);

    Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default);
}

public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendPostPublishedAsync(SendEmailCommand command, CancellationToken cancellationToken = default);
}
