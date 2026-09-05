namespace Blog.Notifications.Core;

public class Subscriber
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UnsubscribedAt { get; set; }

    public string UnsubscribeToken { get; set; } = string.Empty;
}

public enum DeliveryStatus
{
    Pending,

    Queued,

    Sent,

    Failed
}

public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

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
