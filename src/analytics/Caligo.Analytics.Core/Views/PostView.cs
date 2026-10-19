namespace Caligo.Analytics.Core.Views;

public class PostView
{
    public string EventId { get; set; } = string.Empty;

    public int PostId { get; set; }

    public DateTime ViewedAt { get; set; }

    public string? Referrer { get; set; }

    public string? UserId { get; set; }

    public string? VisitorHash { get; set; }
}

public record PostStats(int PostId, long Views, long UniqueVisitors)
{
    public static PostStats Empty(int postId) => new(postId, 0, 0);
}

public record TrendingEntry(int PostId, long Score);
