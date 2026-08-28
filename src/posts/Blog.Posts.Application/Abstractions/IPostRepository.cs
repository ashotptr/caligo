using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;

namespace Blog.Posts.Application.Abstractions;

/// <summary>
/// Persistence for posts.
///
/// Note what this is not: a generic <c>IRepository&lt;T&gt;</c> with <c>GetAll</c> and
/// <c>Find(predicate)</c>. EF Core's DbSet is already a repository and its DbContext already a
/// unit of work, so wrapping them generically buys nothing and costs the ability to write a
/// good query. What earns its keep is a narrow, named interface: it says exactly which
/// database operations this application performs, and it is small enough to fake in a test.
///
/// Reads return projections rather than entities. The list page needs six columns and an
/// author name; materialising whole entities with their graphs to then map them was measurably
/// slower and is why <c>AsNoTracking</c> plus a Select lives on this side of the boundary.
/// </summary>
public interface IPostRepository
{
    Task<PagedResult<PostSummaryDto>> GetPageAsync(
        PostCursor? cursor, int limit, string? authorId, CancellationToken cancellationToken = default);

    Task<PostDetailDto?> GetDetailAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Loads the tracked entity, for use cases that change it.</summary>
    Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default);

    void Add(BlogPost post);

    void Remove(BlogPost post);

    /// <summary>
    /// Commits the current change set and returns the number of rows affected. Domain events
    /// raised by tracked entities are drained here, after the commit succeeds.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

}
