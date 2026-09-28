using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;

namespace Blog.Posts.Application.Abstractions;

public interface IPostRepository
{
    // A null author id pages the public feed (published posts only); an author id pages that
    // author's own posts, scheduled ones included.
    Task<PagedResult<PostSummaryDto>> GetPageAsync(
        PostCursor? cursor, int limit, string? authorId, CancellationToken cancellationToken = default);

    Task<PostDetailDto?> GetDetailAsync(int id, CancellationToken cancellationToken = default);

    Task<PostHeader?> GetHeaderAsync(int id, CancellationToken cancellationToken = default);

    Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default);

    void Add(BlogPost post);

    void Remove(BlogPost post);

    // Adds a scheduled post only if its author is still under the quota. Returns false when the
    // quota is full. Throws ConcurrencyConflictException if the check could not be serialized.
    Task<bool> TryAddScheduledAsync(
        BlogPost post, int maxScheduledPerAuthor, CancellationToken cancellationToken = default);

    // Throws ConcurrencyConflictException when an optimistic-concurrency check fails.
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
