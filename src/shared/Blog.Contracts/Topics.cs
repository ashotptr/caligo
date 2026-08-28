namespace Blog.Contracts;

/// <summary>
/// Kafka topic names and their layout. Versioned in the name (<c>.v1</c>) because a topic is a
/// published contract: a breaking change to the payload means a new topic that consumers move
/// to on their own schedule, not a redefinition of this one underneath them.
/// </summary>
public static class Topics
{
    /// <summary>
    /// Every read of a post. High volume, append-only, and read by more than one consumer
    /// group, which is what makes it a log rather than a queue.
    /// </summary>
    public const string PostViewed = "blog.post.viewed.v1";

    /// <summary>Posts being published, revised and deleted. Low volume, high value.</summary>
    public const string PostLifecycle = "blog.post.lifecycle.v1";

    /// <summary>
    /// Partition counts. Both topics are keyed by post id, so all events for one post land on
    /// one partition and are therefore ordered with respect to each other. The count is the
    /// ceiling on consumer parallelism for a group, and cannot be lowered later.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Partitions = new Dictionary<string, int>
    {
        [PostViewed] = 6,
        [PostLifecycle] = 3
    };

    /// <summary>
    /// How long the log is kept. Retention is what makes replay possible: a new consumer group
    /// can rebuild its state from the beginning of the window rather than starting empty.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Retention = new Dictionary<string, TimeSpan>
    {
        [PostViewed] = TimeSpan.FromDays(7),
        [PostLifecycle] = TimeSpan.FromDays(30)
    };
}

/// <summary>Message header names, kept together so producer and consumer cannot disagree.</summary>
public static class EventHeaders
{
    public const string EventType = "event-type";
    public const string SchemaVersion = "schema-version";

    /// <summary>W3C trace context, so a trace can span the producer and the consumer.</summary>
    public const string TraceParent = "traceparent";
}
