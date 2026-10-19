namespace Blog.Contracts;

public static class Topics
{
    public const string PostViewed = "blog.post.viewed.v1";

    public const string PostLifecycle = "blog.post.lifecycle.v1";

    public const string PostClapped = "blog.post.clapped.v1";

    public const string PlaybackEvents = "blog.playback.events.v1";

    public static readonly IReadOnlyDictionary<string, int> Partitions = new Dictionary<string, int>
    {
        [PostViewed] = 6,
        [PostLifecycle] = 3,
        [PostClapped] = 6,
        [PlaybackEvents] = 3
    };

    public static readonly IReadOnlyDictionary<string, TimeSpan> Retention = new Dictionary<string, TimeSpan>
    {
        [PostViewed] = TimeSpan.FromDays(7),
        [PostLifecycle] = TimeSpan.FromDays(30),
        [PostClapped] = TimeSpan.FromDays(7),
        [PlaybackEvents] = TimeSpan.FromDays(7)
    };
}

public static class EventHeaders
{
    public const string EventType = "event-type";
    public const string SchemaVersion = "schema-version";

    public const string TraceParent = "traceparent";
}