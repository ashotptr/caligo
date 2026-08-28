namespace Blog.Notifications.Core;

public class Subscriber
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UnsubscribedAt { get; set; }

    /// <summary>
    /// Lets someone unsubscribe from a link in an email without signing in. Random rather than
    /// derived from the address, so possessing one token says nothing about any other.
    /// </summary>
    public string UnsubscribeToken { get; set; } = string.Empty;
}

public enum DeliveryStatus
{
    /// <summary>Claimed, but not yet handed to the broker.</summary>
    Pending,

    /// <summary>On the queue. A worker will pick it up.</summary>
    Queued,

    Sent,

    /// <summary>Exhausted its retries and was parked in the dead-letter queue.</summary>
    Failed
}

/// <summary>
/// One email owed to one subscriber for one event.
///
/// The unique constraint on (EventId, SubscriberId) is what makes replaying the Kafka topic
/// safe. That replay is harmless for the analytics projections - they recount - but re-sending
/// every announcement email to every subscriber is not something a recount undoes. The same
/// stream therefore needs a different guarantee on this side, and this row is it.
/// </summary>
public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identity of the integration event that caused this, carried from Kafka.</summary>
    public Guid EventId { get; set; }

    public Guid SubscriberId { get; set; }

    public int PostId { get; set; }

    public string PostTitle { get; set; } = string.Empty;

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// The command put on RabbitMQ: send this specific email to this specific person.
///
/// Note how much smaller its scope is than the Kafka event that produced it. The event says
/// "a post was published" once, to whoever cares. This says "deliver this" to exactly one
/// worker, and carries the identity needed to retry it, give up on it, or recognise that it
/// has already been done.
/// </summary>
public record SendEmailCommand(
    Guid DeliveryId,
    Guid EventId,
    Guid SubscriberId,
    string Email,
    string? DisplayName,
    int PostId,
    string PostTitle,
    string UnsubscribeToken)
{
    public int Attempt { get; init; }
}
