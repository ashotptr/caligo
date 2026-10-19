using Caligo.Posts.Application.Posts;
using Caligo.Posts.Domain.Posts;
using Caligo.SharedKernel;

namespace Caligo.Posts.Application.Abstractions;

public interface IPostRepository
{
    Task<PagedResult<PostSummaryDto>> GetPageAsync(
        PostCursor? cursor, int limit, string? authorId, CancellationToken cancellationToken = default);

    Task<PagedResult<PostSummaryDto>> GetPageForContentAsync(
        int contentId, PostCursor? cursor, int limit, CancellationToken cancellationToken = default);

    Task<PostDetailDto?> GetDetailAsync(int id, CancellationToken cancellationToken = default);

    Task<PostHeader?> GetHeaderAsync(int id, CancellationToken cancellationToken = default);

    Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default);

    void Add(BlogPost post);

    void Remove(BlogPost post);

    Task<bool> TryAddScheduledAsync(
        BlogPost post, int maxScheduledPerAuthor, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
