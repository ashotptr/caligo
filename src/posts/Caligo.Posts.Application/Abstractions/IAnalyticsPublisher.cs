namespace Caligo.Posts.Application.Abstractions;

public class PostViewedEvent
{
    public int PostId { get; set; }

    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;

    public string? Referrer { get; set; }

    public string? UserId { get; set; }

    public string? VisitorHash { get; set; }
}

public class PostClappedEvent
{
    public int PostId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public int Delta { get; set; }

    public int ReaderTotal { get; set; }

    public bool FirstClap { get; set; }

    public DateTime ClappedAt { get; set; } = DateTime.UtcNow;
}

public interface IAnalyticsPublisher
{
    void PublishPostViewed(PostViewedEvent viewed);

    void PublishPostClapped(PostClappedEvent clapped);
}
