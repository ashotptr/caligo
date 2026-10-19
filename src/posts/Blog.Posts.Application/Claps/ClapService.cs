using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.SharedKernel;

namespace Blog.Posts.Application.Claps;

public record ClapResult(int Applied, int Mine, int Max);

public class ClapService
{
    private readonly IPostRepository _posts;
    private readonly IClapStore _claps;
    private readonly IAnalyticsPublisher _analytics;
    private readonly TimeProvider _time;

    public ClapService(IPostRepository posts, IClapStore claps, IAnalyticsPublisher analytics, TimeProvider time)
    {
        _posts = posts;
        _claps = claps;
        _analytics = analytics;
        _time = time;
    }

    public async Task<Result<ClapResult>> ClapAsync(
        int postId, int count, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<ClapResult>.Unauthorized();
        }

        if (!ClapRules.IsValidRequest(count))
        {
            return Result<ClapResult>.Invalid($"Clap between 1 and {ClapRules.MaxPerReader} times.");
        }

        var post = await _posts.GetHeaderAsync(postId, cancellationToken);

        if (post is not { IsPublished: true })
        {
            return Result<ClapResult>.NotFound();
        }

        if (post.AuthorId == caller.UserId)
        {
            return Result<ClapResult>.Forbidden("You can't clap for your own post.");
        }

        var outcome = await _claps.ClapAsync(postId, caller.UserId!, count, cancellationToken);

        if (outcome is null)
        {
            return Result<ClapResult>.NotFound();
        }

        if (outcome.Applied > 0)
        {
            _analytics.PublishPostClapped(new PostClappedEvent
            {
                PostId = postId,
                UserId = caller.UserId!,
                Delta = outcome.Applied,
                ReaderTotal = outcome.ReaderTotal,
                FirstClap = outcome.FirstClap,
                ClappedAt = _time.GetUtcNow().UtcDateTime
            });
        }

        return Result<ClapResult>.Ok(
            new ClapResult(outcome.Applied, outcome.ReaderTotal, ClapRules.MaxPerReader));
    }

    public async Task<Result<ClapTally>> TallyAsync(
        int postId, Caller caller, CancellationToken cancellationToken = default)
    {
        var post = await _posts.GetHeaderAsync(postId, cancellationToken);

        if (post is not { IsPublished: true })
        {
            return Result<ClapTally>.NotFound();
        }

        return Result<ClapTally>.Ok(await _claps.TallyAsync(postId, caller.UserId, cancellationToken));
    }

    public Task<ClapAudit> AuditAsync(int? postId, CancellationToken cancellationToken = default) =>
        _claps.AuditAsync(postId, cancellationToken);
}
