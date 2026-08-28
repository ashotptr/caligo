namespace Blog.Notifications.Core;

public interface ISubscriberStore
{
    Task<IReadOnlyList<Subscriber>> ActiveAsync(CancellationToken cancellationToken = default);

    Task<Subscriber> SubscribeAsync(string email, string? displayName, CancellationToken cancellationToken = default);

    /// <summary>Returns false when the token matches nothing.</summary>
    Task<bool> UnsubscribeAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// The record of what has been owed, queued and sent.
///
/// It does double duty. The unique key deduplicates a replayed event, and the Pending status
/// is a small outbox: a row is written before the command reaches RabbitMQ, so a crash in
/// between leaves evidence that something is owed rather than losing it silently.
/// </summary>
public interface IDeliveryLedger
{
    /// <summary>
    /// Records one delivery per subscriber for this event, skipping any that already exist.
    /// Returns only the rows that were genuinely new, which are the ones to enqueue.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> ClaimAsync(
        Guid eventId, int postId, string postTitle,
        IReadOnlyList<Subscriber> subscribers,
        CancellationToken cancellationToken = default);

    Task MarkQueuedAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken cancellationToken = default);

    Task MarkSentAsync(Guid deliveryId, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default);

    Task RecordAttemptAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows written but never handed to the broker, because the process died in between.
    /// Re-enqueued by a sweeper rather than left owed forever.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> StrandedAsync(
        TimeSpan olderThan, int limit, CancellationToken cancellationToken = default);

    Task<Subscriber?> SubscriberAsync(Guid subscriberId, CancellationToken cancellationToken = default);
}

/// <summary>Puts a command on the queue for a worker to execute.</summary>
public interface ICommandQueue
{
    bool IsConfigured { get; }

    Task EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-queues a failed command with a delay that grows per attempt, or parks it when the
    /// attempts are exhausted.
    /// </summary>
    Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default);
}

public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendPostPublishedAsync(SendEmailCommand command, CancellationToken cancellationToken = default);
}
