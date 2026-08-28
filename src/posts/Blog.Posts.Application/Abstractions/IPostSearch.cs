using Blog.Posts.Application.Posts;

namespace Blog.Posts.Application.Abstractions;

/// <summary>
/// Full-text search differs fundamentally between the supported providers: PostgreSQL has a
/// real inverted index, SQLite (used for local runs and tests) does not. Implementations pick
/// the strategy that fits the provider, and neither leaks IQueryable across this boundary.
/// </summary>
public interface IPostSearch
{
    Task<IReadOnlyList<PostSummaryDto>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default);
}
