namespace Caligo.Analytics.Core.Views;

public interface IViewStore
{
    bool IsConfigured { get; }

    Task RecordAsync(PostView view, CancellationToken cancellationToken = default);

    Task<int> RecordManyAsync(IReadOnlyList<PostView> views, CancellationToken cancellationToken = default);

    Task<PostStats> ForPostAsync(int postId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostStats>> ForPostsAsync(
        IReadOnlyList<int> postIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostStats>> TopPostsAsync(
        int days, int limit, CancellationToken cancellationToken = default);
}

public interface ITrendingStore
{
    bool IsConfigured { get; }

    Task IncrementAsync(int postId, DateTime viewedAt, CancellationToken cancellationToken = default);

    Task IncrementManyAsync(IReadOnlyList<PostView> views, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrendingEntry>> TopAsync(
        int windowHours, int limit, CancellationToken cancellationToken = default);
}
