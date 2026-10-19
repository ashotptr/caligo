using Caligo.Posts.Application.Posts;

namespace Caligo.Posts.Application.Abstractions;

public interface IPostSearch
{
    Task<IReadOnlyList<PostSummaryDto>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default);
}
