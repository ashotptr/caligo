using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;

namespace Blog.Posts.Application.Abstractions;

public interface IPostRepository
{
    Task<PagedResult<PostSummaryDto>> GetPageAsync(
        PostCursor? cursor, int limit, string? authorId, CancellationToken cancellationToken = default);

    Task<PostDetailDto?> GetDetailAsync(int id, CancellationToken cancellationToken = default);

    Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default);

    void Add(BlogPost post);

    void Remove(BlogPost post);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
