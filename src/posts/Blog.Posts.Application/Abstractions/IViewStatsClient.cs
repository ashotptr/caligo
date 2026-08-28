namespace Blog.Posts.Application.Abstractions;

public record ViewStats(int PostId, long Views, long UniqueVisitors);

public record TrendingPost(int PostId, long Score);

/// <summary>
/// Read access to the analytics service.
///
/// Every method may return null, and callers must treat that as "no numbers available" rather
/// than as an error. Analytics is a separate process: if it is slow, restarting or down, a
/// post must still render - just without a view count. Encoding that in the return type means
/// the degraded path cannot be forgotten.
/// </summary>
public interface IViewStatsClient
{
    bool IsConfigured { get; }

    Task<ViewStats?> GetForPostAsync(int postId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, ViewStats>?> GetForPostsAsync(
        IReadOnlyList<int> postIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ViewStats>?> GetTopPostsAsync(
        int days, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams trending snapshots until the caller stops enumerating. Yields nothing when the
    /// analytics service is unavailable.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<TrendingPost>> StreamTrendingAsync(
        int windowHours, int limit, CancellationToken cancellationToken = default);
}
