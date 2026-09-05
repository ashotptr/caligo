namespace Blog.Contracts.Events;

public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public int SchemaVersion { get; init; } = 1;

    public abstract string EventType { get; }

    public abstract string PartitionKey { get; }
}

public abstract record PostEvent : IntegrationEvent
{
    public int PostId { get; init; }

    public override string PartitionKey => PostId.ToString();
}

public sealed record PostViewedEvent : PostEvent
{
    public override string EventType => "post.viewed";

    public string? Referrer { get; init; }

    public string? UserId { get; init; }

    public string? VisitorHash { get; init; }
}

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