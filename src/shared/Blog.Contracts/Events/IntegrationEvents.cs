namespace Blog.Contracts.Events;

/// <summary>
/// Something that happened, published for other services to react to.
///
/// Distinct from a domain event: a domain event is internal, expressed in the language of the
/// model, and free to change with a refactor. An integration event is a published contract -
/// once another service consumes it, its shape can only be extended, never narrowed.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>
    /// Stable identity for this occurrence. Delivery is at-least-once end to end, so every
    /// consumer needs this to recognise a message it has already handled.
    /// </summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Lets a consumer reject a payload written by a newer producer it does not understand,
    /// rather than silently mis-reading it.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Name carried in the <c>event-type</c> header, used to route on the consumer side.</summary>
    public abstract string EventType { get; }

    /// <summary>
    /// Partition key. Everything about one post shares a key, so its events stay ordered
    /// relative to each other however many consumers are running.
    /// </summary>
    public abstract string PartitionKey { get; }
}

public abstract record PostEvent : IntegrationEvent
{
    public int PostId { get; init; }

    public override string PartitionKey => PostId.ToString();
}

/// <summary>
/// A post was read.
///
/// Published straight to Kafka rather than through the outbox, deliberately: a view is not a
/// state change, nothing depends on it being exact, and paying for a database round trip on
/// every page render to guarantee delivery of a counter would be the wrong trade.
/// </summary>
public sealed record PostViewedEvent : PostEvent
{
    public override string EventType => "post.viewed";

    public string? Referrer { get; init; }

    public string? UserId { get; init; }

    /// <summary>
    /// Daily-rotating hash of address and user agent. Enough to count a visitor once per day,
    /// not enough to identify them or to follow them across days.
    /// </summary>
    public string? VisitorHash { get; init; }
}

/// <summary>
/// A post became publicly readable. Goes through the outbox: losing this means a post exists
/// that was never announced, and no amount of retrying at the edge can detect that.
/// </summary>
public sealed record PostPublishedEvent : PostEvent
{
    public override string EventType => "post.published";

    public string Title { get; init; } = string.Empty;

    public string? AuthorId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

public sealed record PostUpdatedEvent : PostEvent
{
    public override string EventType => "post.updated";

    public string Title { get; init; } = string.Empty;
}

public sealed record PostDeletedEvent : PostEvent
{
    public override string EventType => "post.deleted";
}
