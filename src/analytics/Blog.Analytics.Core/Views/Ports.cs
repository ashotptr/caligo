namespace Blog.Analytics.Core.Views;

/// <summary>
/// Durable storage of individual views, and the aggregations over them. Backed by a document
/// store: the rows are unbounded, append-only, never updated, and queried by grouping rather
/// than by relation.
/// </summary>
public interface IViewStore
{
    bool IsConfigured { get; }

    Task RecordAsync(PostView view, CancellationToken cancellationToken = default);

    /// <summary>Stores a batch, ignoring views already recorded. Returns how many were new.</summary>
    Task<int> RecordManyAsync(IReadOnlyList<PostView> views, CancellationToken cancellationToken = default);

    Task<PostStats> ForPostAsync(int postId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostStats>> ForPostsAsync(
        IReadOnlyList<int> postIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostStats>> TopPostsAsync(
        int days, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rolling "trending now" rollup.
///
/// Deliberately separate from <see cref="IViewStore"/> and backed by Redis rather than the
/// document store. Trending is a small, hot, constantly changing leaderboard read far more
/// often than it is precise; recomputing it by aggregating raw views on every request would
/// scan the whole window each time. It is also disposable - if Redis loses it, it rebuilds
/// from the next hour of traffic, or from a Kafka replay.
/// </summary>
public interface ITrendingStore
{
    bool IsConfigured { get; }

    /// <summary>Adds one view to the current time bucket.</summary>
    Task IncrementAsync(int postId, DateTime viewedAt, CancellationToken cancellationToken = default);

    Task IncrementManyAsync(IReadOnlyList<PostView> views, CancellationToken cancellationToken = default);

    /// <summary>Highest-scoring posts across the last <paramref name="windowHours"/> hours.</summary>
    Task<IReadOnlyList<TrendingEntry>> TopAsync(
        int windowHours, int limit, CancellationToken cancellationToken = default);
}
