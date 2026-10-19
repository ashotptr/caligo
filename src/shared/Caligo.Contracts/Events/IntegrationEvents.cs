namespace Caligo.Contracts.Events;

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

public sealed record PostClappedEvent : PostEvent
{
    public override string EventType => "post.clapped";

    public string UserId { get; init; } = string.Empty;

    public int Delta { get; init; }

    public int ReaderTotal { get; init; }

    public bool FirstClap { get; init; }
}

public sealed record PostPublishedEvent : PostEvent
{
    public override string EventType => "post.published";

    public string Title { get; init; } = string.Empty;

    public string? AuthorId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public int? ContentId { get; init; }
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

public sealed record PlaybackEvent : IntegrationEvent
{
    public override string EventType => "playback.event";

    public override string PartitionKey => SessionId.ToString("N");

    public Guid SessionId { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public long Seq { get; init; }

    public int ContentId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Delivery { get; init; } = string.Empty;

    public float Position { get; init; }

    public int DurationMs { get; init; }

    public int FromHeight { get; init; }

    public int ToHeight { get; init; }

    public string Detail { get; init; } = string.Empty;
}
