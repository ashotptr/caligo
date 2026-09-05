using Blog.Posts.Application.Posts;

namespace Blog.Posts.Application.Abstractions;

public interface IPostSearch
{
    Task<IReadOnlyList<PostSummaryDto>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default);
}
