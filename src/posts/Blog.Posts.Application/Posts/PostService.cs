using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Common;
using Blog.Posts.Domain.Posts;

namespace Blog.Posts.Application.Posts;

public readonly record struct Caller(string? UserId, bool IsAdministrator)
{
    public static readonly Caller Anonymous = new(null, false);

    public bool IsAuthenticated => UserId is not null;
}

public record WritePostCommand(string Title, string Content, IReadOnlyList<string>? Tags);

public class PostService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxSearchResults = 50;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IPostRepository _posts;
    private readonly IPostCache _cache;
    private readonly IPostSearch _search;
    private readonly IAnalyticsPublisher _analytics;
    private readonly TimeProvider _time;

    public PostService(
        IPostRepository posts,
        IPostCache cache,
        IPostSearch search,
        IAnalyticsPublisher analytics,
        TimeProvider time)
    {
        _posts = posts;
        _cache = cache;
        _search = search;
        _analytics = analytics;
        _time = time;
    }

    public async Task<Result<PagedResult<PostSummaryDto>>> GetPageAsync(
        string? cursor, int limit, bool mineOnly, Caller caller, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (mineOnly && !caller.IsAuthenticated)
        {
            return Result<PagedResult<PostSummaryDto>>.Unauthorized();
        }

        var authorId = mineOnly ? caller.UserId : null;

        var cacheable = authorId is null;

        var version = await _cache.GetVersionAsync(cancellationToken);
        var cacheKey = $"posts:{version}:page:{cursor ?? "first"}:{limit}";

        if (cacheable)
        {
            var cached = await _cache.GetAsync<PagedResult<PostSummaryDto>>(cacheKey, cancellationToken);

            if (cached is not null)
            {
                return Result<PagedResult<PostSummaryDto>>.Ok(cached);
            }
        }

        var page = await _posts.GetPageAsync(PostCursor.TryDecode(cursor), limit, authorId, cancellationToken);

        if (cacheable)
        {
            await _cache.SetAsync(cacheKey, page, CacheTtl, cancellationToken);
        }

        return Result<PagedResult<PostSummaryDto>>.Ok(page);
    }

    public async Task<Result<PostDetailDto>> GetAsync(
        int id, ViewContext view, Caller caller, CancellationToken cancellationToken = default)
    {
        var version = await _cache.GetVersionAsync(cancellationToken);
        var cacheKey = $"posts:{version}:detail:{id}";

        var post = await _cache.GetAsync<PostDetailDto>(cacheKey, cancellationToken);

        if (post is null)
        {
            post = await _posts.GetDetailAsync(id, cancellationToken);

            if (post is null)
            {
                return Result<PostDetailDto>.NotFound();
            }

            await _cache.SetAsync(cacheKey, post, CacheTtl, cancellationToken);
        }

        _analytics.PublishPostViewed(new PostViewedEvent
        {
            PostId = post.Id,
            ViewedAt = _time.GetUtcNow().UtcDateTime,
            Referrer = view.Referrer,
            UserId = caller.UserId,
            VisitorHash = view.VisitorHash
        });

        return Result<PostDetailDto>.Ok(post);
    }

    public async Task<Result<IReadOnlyList<PostSummaryDto>>> SearchAsync(
        string? query, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result<IReadOnlyList<PostSummaryDto>>.Invalid("Search query cannot be empty.");
        }

        limit = Math.Clamp(limit, 1, MaxSearchResults);

        return Result<IReadOnlyList<PostSummaryDto>>.Ok(
            await _search.SearchAsync(query.Trim(), limit, cancellationToken));
    }

    public async Task<Result<PostDetailDto>> CreateAsync(
        WritePostCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<PostDetailDto>.Unauthorized();
        }

        var post = BlogPost.Publish(
            command.Title, command.Content, command.Tags, caller.UserId, _time.GetUtcNow().UtcDateTime);

        _posts.Add(post);

        await _posts.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(cancellationToken);

        var detail = await _posts.GetDetailAsync(post.Id, cancellationToken);

        return detail is null
            ? Result<PostDetailDto>.NotFound()
            : Result<PostDetailDto>.Ok(detail);
    }

    public async Task<Result> UpdateAsync(
        int id, WritePostCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        var post = await _posts.FindAsync(id, cancellationToken);

        if (post is null)
        {
            return Result.NotFound();
        }

        if (!post.CanBeModifiedBy(caller.UserId, caller.IsAdministrator))
        {
            return Result.Forbidden();
        }

        post.Revise(command.Title, command.Content, command.Tags);

        await _posts.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(cancellationToken);

        return Result.Ok();
    }

    public async Task<Result> DeleteAsync(int id, Caller caller, CancellationToken cancellationToken = default)
    {
        var post = await _posts.FindAsync(id, cancellationToken);

        if (post is null)
        {
            return Result.NotFound();
        }

        if (!post.CanBeModifiedBy(caller.UserId, caller.IsAdministrator))
        {
            return Result.Forbidden();
        }

        post.MarkDeleted();

        _posts.Remove(post);

        await _posts.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(cancellationToken);

        return Result.Ok();
    }
}

public readonly record struct ViewContext(string? Referrer, string? VisitorHash)
{
    public static readonly ViewContext Empty = new(null, null);
}