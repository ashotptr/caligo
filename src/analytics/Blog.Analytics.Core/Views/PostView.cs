namespace Blog.Analytics.Core.Views;

/// <summary>One recorded read of a post.</summary>
public class PostView
{
    /// <summary>
    /// Identity of the event that produced this row. Kafka delivery is at-least-once, so this
    /// is what makes a redelivered batch a no-op rather than a double count.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    public int PostId { get; set; }

    public DateTime ViewedAt { get; set; }

    public string? Referrer { get; set; }

    public string? UserId { get; set; }

    /// <summary>
    /// Daily-rotating hash of address and user agent. Enough to count a visitor once per day,
    /// not enough to identify them or follow them across days.
    /// </summary>
    public string? VisitorHash { get; set; }
}

public record PostStats(int PostId, long Views, long UniqueVisitors)
{
    public static PostStats Empty(int postId) => new(postId, 0, 0);
}

public record TrendingEntry(int PostId, long Score);
