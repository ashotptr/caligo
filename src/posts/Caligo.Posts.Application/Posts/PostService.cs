using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Application.Common;
using Caligo.Posts.Domain.Posts;
using Caligo.SharedKernel;

namespace Caligo.Posts.Application.Posts;

public record WritePostCommand(
    string Title,
    string Content,
    IReadOnlyList<string>? Tags,
    DateTime? PublishAt = null,
    int? ExpectedVersion = null,
    int? ContentId = null);

public class PostService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxSearchResults = 50;

    public const string ListScope = "posts:lists";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IPostRepository _posts;
    private readonly IPostCache _cache;
    private readonly IPostSearch _search;
    private readonly IAnalyticsPublisher _analytics;
    private readonly TimeProvider _time;
    private readonly PostingRules _rules;
    private readonly IContentLookup _contents;

    public PostService(
        IPostRepository posts,
        IPostCache cache,
        IPostSearch search,
        IAnalyticsPublisher analytics,
        TimeProvider time,
        PostingRules rules,
        IContentLookup contents)
    {
        _posts = posts;
        _cache = cache;
        _search = search;
        _analytics = analytics;
        _time = time;
        _rules = rules;
        _contents = contents;
    }

    public async Task<Result<PagedResult<PostSummaryDto>>> GetPageAsync(
        string? cursor, int limit, bool mineOnly, Caller caller, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (mineOnly && !caller.IsAuthenticated)
        {
            return Result<PagedResult<PostSummaryDto>>.Unauthorized();
        }

        var from = PostCursor.TryDecode(cursor);

        if (mineOnly)
        {
            return Result<PagedResult<PostSummaryDto>>.Ok(await WithContentTitlesAsync(
                await _posts.GetPageAsync(from, limit, caller.UserId, cancellationToken), cancellationToken));
        }

        var generation = await _cache.GetGenerationAsync(ListScope, cancellationToken);

        var page = await _cache.GetOrLoadAsync(
            $"posts:list:{generation}:page:{cursor ?? "first"}:{limit}",
            async token => await WithContentTitlesAsync(await _posts.GetPageAsync(from, limit, null, token), token),
            _ => true,
            CacheTtl,
            cancellationToken);

        return Result<PagedResult<PostSummaryDto>>.Ok(page ?? new PagedResult<PostSummaryDto>());
    }

    public async Task<Result<PagedResult<PostSummaryDto>>> GetReviewsAsync(
        int contentId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        var from = PostCursor.TryDecode(cursor);
        var generation = await _cache.GetGenerationAsync(ListScope, cancellationToken);

        var page = await _cache.GetOrLoadAsync(
            $"posts:list:{generation}:content:{contentId}:page:{cursor ?? "first"}:{limit}",
            async token => await WithContentTitlesAsync(
                await _posts.GetPageForContentAsync(contentId, from, limit, token), token),
            _ => true,
            CacheTtl,
            cancellationToken);

        return Result<PagedResult<PostSummaryDto>>.Ok(page ?? new PagedResult<PostSummaryDto>());
    }

    public async Task<Result<PostDetailDto>> GetAsync(
        int id, ViewContext view, Caller caller, CancellationToken cancellationToken = default)
    {
        var generation = await _cache.GetGenerationAsync(PostScope(id), cancellationToken);

        var post = await _cache.GetOrLoadAsync(
            $"posts:detail:{id}:{generation}",
            async token => await WithContentTitleAsync(await _posts.GetDetailAsync(id, token), token),
            detail => detail.IsPublished,
            CacheTtl,
            cancellationToken);

        if (post is null)
        {
            return Result<PostDetailDto>.NotFound();
        }

        if (!post.IsPublished)
        {
            return caller.CanManage(post.AuthorId)
                ? Result<PostDetailDto>.Ok(post)
                : Result<PostDetailDto>.NotFound();
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

        if (!await ContentIsKnownAsync(command.ContentId, cancellationToken))
        {
            return Result<PostDetailDto>.Invalid(UnknownContent);
        }

        var now = _time.GetUtcNow().UtcDateTime;

        if (command.PublishAt is { } publishAt && publishAt > now)
        {
            return await ScheduleAsync(command, publishAt, now, caller, cancellationToken);
        }

        var post = BlogPost.Publish(command.Title, command.Content, command.Tags, caller.UserId, now);

        post.LinkToContent(command.ContentId);

        _posts.Add(post);

        await _posts.SaveChangesAsync(cancellationToken);
        await _cache.InvalidateAsync(ListScope, cancellationToken);

        return await DetailOfAsync(post.Id, cancellationToken);
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

        if (command.ExpectedVersion is { } expected && expected != post.Version)
        {
            return Result.PreconditionFailed("The post has changed since you loaded it.");
        }

        if (command.PublishAt is { } publishAt)
        {
            if (post.IsPublished)
            {
                return Result.Invalid("The post is already published.");
            }

            if (publishAt > _time.GetUtcNow().UtcDateTime + _rules.MaxScheduleAhead)
            {
                return Result.Invalid(TooFarAhead());
            }

            post.Reschedule(publishAt);
        }

        if (command.ContentId != post.ContentId && !await ContentIsKnownAsync(command.ContentId, cancellationToken))
        {
            return Result.Invalid(UnknownContent);
        }

        post.LinkToContent(command.ContentId);
        post.Revise(command.Title, command.Content, command.Tags);

        try
        {
            await _posts.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result.Conflict("The post was changed or deleted by someone else. Reload it and try again.");
        }

        await _cache.InvalidateAsync(PostScope(id), cancellationToken);

        if (post.IsPublished)
        {
            await _cache.InvalidateAsync(ListScope, cancellationToken);
        }

        return Result.Ok();
    }

    public async Task<Result> DeleteAsync(
        int id, Caller caller, int? expectedVersion = null, CancellationToken cancellationToken = default)
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

        if (expectedVersion is { } expected && expected != post.Version)
        {
            return Result.PreconditionFailed("The post has changed since you loaded it.");
        }

        var wasPublished = post.IsPublished;

        post.MarkDeleted();

        _posts.Remove(post);

        try
        {
            await _posts.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result.Conflict("The post was changed or deleted by someone else. Reload it and try again.");
        }

        await _cache.InvalidateAsync(PostScope(id), cancellationToken);

        if (wasPublished)
        {
            await _cache.InvalidateAsync(ListScope, cancellationToken);
        }

        return Result.Ok();
    }

    private async Task<Result<PostDetailDto>> ScheduleAsync(
        WritePostCommand command, DateTime publishAt, DateTime now, Caller caller, CancellationToken cancellationToken)
    {
        if (publishAt > now + _rules.MaxScheduleAhead)
        {
            return Result<PostDetailDto>.Invalid(TooFarAhead());
        }

        var post = BlogPost.Schedule(command.Title, command.Content, command.Tags, caller.UserId, publishAt);

        post.LinkToContent(command.ContentId);

        bool added;

        try
        {
            added = await _posts.TryAddScheduledAsync(post, _rules.MaxScheduledPerAuthor, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result<PostDetailDto>.Conflict("Too many scheduling requests at once. Try again.");
        }

        if (!added)
        {
            return Result<PostDetailDto>.Conflict(
                $"You already have {_rules.MaxScheduledPerAuthor} scheduled posts. Publish or delete one first.");
        }

        return await DetailOfAsync(post.Id, cancellationToken);
    }

    private async Task<Result<PostDetailDto>> DetailOfAsync(int id, CancellationToken cancellationToken)
    {
        var detail = await WithContentTitleAsync(await _posts.GetDetailAsync(id, cancellationToken), cancellationToken);

        return detail is null
            ? Result<PostDetailDto>.NotFound()
            : Result<PostDetailDto>.Ok(detail);
    }

    public const string UnknownContent = "That title is not in the catalog.";

    private async Task<bool> ContentIsKnownAsync(int? contentId, CancellationToken cancellationToken) =>
        contentId is null || (contentId > 0 && await _contents.ExistsAsync(contentId.Value, cancellationToken));

    private async Task<PagedResult<PostSummaryDto>> WithContentTitlesAsync(
        PagedResult<PostSummaryDto> page, CancellationToken cancellationToken)
    {
        var ids = page.Items.Where(p => p.ContentId is not null).Select(p => p.ContentId!.Value).Distinct().ToList();

        if (ids.Count == 0)
        {
            return page;
        }

        var titles = await _contents.GetTitlesAsync(ids, cancellationToken);

        foreach (var post in page.Items)
        {
            if (post.ContentId is { } contentId && titles.TryGetValue(contentId, out var title))
            {
                post.ContentTitle = title;
            }
        }

        return page;
    }

    private async Task<PostDetailDto?> WithContentTitleAsync(PostDetailDto? detail, CancellationToken cancellationToken)
    {
        if (detail?.ContentId is not { } contentId)
        {
            return detail;
        }

        var titles = await _contents.GetTitlesAsync(new[] { contentId }, cancellationToken);

        detail.ContentTitle = titles.GetValueOrDefault(contentId);

        return detail;
    }

    public static string PostScope(int id) => $"posts:{id}";

    private string TooFarAhead() =>
        $"A post can be scheduled at most {_rules.MaxScheduleAhead.TotalDays:0} days ahead.";
}

public readonly record struct ViewContext(string? Referrer, string? VisitorHash)
{
    public static readonly ViewContext Empty = new(null, null);
}
