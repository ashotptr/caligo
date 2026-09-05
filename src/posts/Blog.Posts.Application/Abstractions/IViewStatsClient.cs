namespace Blog.Posts.Application.Abstractions;

public record ViewStats(int PostId, long Views, long UniqueVisitors);

public record TrendingPost(int PostId, long Score);

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
}