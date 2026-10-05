namespace Blog.Posts.Application.Abstractions;

public record ViewStats(int PostId, long Views, long UniqueVisitors);

public record TrendingPost(int PostId, long Score);

public enum SeriesGranularity
{
    Minute,
    Hour,
    Day
}

public enum WindowRanking
{
    Views,
    Claps
}

public record SeriesPoint(DateTime BucketStart, long Views, long UniqueVisitors, long Claps);

public record WindowPost(int PostId, long Views, long UniqueVisitors, long Claps);

public record Engagement(int PostId, long Views, long UniqueVisitors, long Clappers, long Claps, int Days);

public interface IViewStatsClient
{
    bool IsConfigured { get; }

    Task<ViewStats?> GetForPostAsync(int postId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, ViewStats>?> GetForPostsAsync(
        IReadOnlyList<int> postIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ViewStats>?> GetTopPostsAsync(
        int days, int limit, CancellationToken cancellationToken = default);

    IAsyncEnumerable<IReadOnlyList<TrendingPost>> StreamTrendingAsync(
        int windowHours, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeriesPoint>?> GetSeriesAsync(
        int postId, DateTime from, DateTime to, SeriesGranularity granularity,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowPost>?> GetTopInWindowAsync(
        int minutes, WindowRanking ranking, int limit, CancellationToken cancellationToken = default);

    Task<Engagement?> GetEngagementAsync(int postId, int days, CancellationToken cancellationToken = default);
}