namespace Blog.Notifications.Core;

public interface ISubscriberStore
{
    Task<Subscriber> SubscribeAsync(string email, string? displayName, CancellationToken cancellationToken = default);

    Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default);
}

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

    Task<bool> RunExclusiveAsync(string lockName, Func<Task> work, CancellationToken cancellationToken = default);
}

public interface ICommandQueue
{
    bool IsConfigured { get; }

    Task<bool> EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default);

    Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default);
}

public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendPostPublishedAsync(SendEmailCommand command, CancellationToken cancellationToken = default);
}
