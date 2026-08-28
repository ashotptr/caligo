namespace Blog.Posts.Application.Abstractions;

public class PostViewedEvent
{
    public int PostId { get; set; }

    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;

    public string? Referrer { get; set; }

    public string? UserId { get; set; }

    public string? VisitorHash { get; set; }
}

/// <summary>
/// Records that a post was read. Implementations must not throw and must not block the
/// request: a dropped view is acceptable, a slow page render is not.
/// </summary>
public interface IAnalyticsPublisher
{
    void PublishPostViewed(PostViewedEvent viewed);
}
