using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[EnableRateLimiting("reads")]
public class AnalyticsController : ControllerBase
{
    private readonly IViewStatsClient _analytics;
    private readonly ApplicationDbContext _context;

    public AnalyticsController(IViewStatsClient analytics, ApplicationDbContext context)
    {
        _analytics = analytics;
        _context = context;
    }

    [HttpGet("posts/{id}")]
    public async Task<IActionResult> ForPost(int id, CancellationToken cancellationToken)
    {
        var stats = await _analytics.GetForPostAsync(id, cancellationToken);

        return Ok(new
        {
            postId = id,
            views = stats?.Views ?? 0,
            uniqueVisitors = stats?.UniqueVisitors ?? 0,
            enabled = stats is not null
        });
    }

    [HttpGet("posts")]
    public async Task<IActionResult> ForPosts([FromQuery] string ids, CancellationToken cancellationToken)
    {
        var postIds = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .Take(100)
            .ToList();

        if (postIds.Count == 0)
        {
            return BadRequest("Provide a comma-separated list of post ids.");
        }

        var stats = await _analytics.GetForPostsAsync(postIds, cancellationToken);

        return Ok(new
        {
            enabled = stats is not null,
            posts = postIds.Select(id =>
            {
                stats?.TryGetValue(id, out var found);
                var value = stats is not null && stats.TryGetValue(id, out var s) ? s : null;

                return new
                {
                    postId = id,
                    views = value?.Views ?? 0,
                    uniqueVisitors = value?.UniqueVisitors ?? 0
                };
            })
        });
    }

    [HttpGet("top")]
    [Authorize(Roles = "Admin,Writer")]
    public async Task<IActionResult> Top(
        [FromQuery] int days = 7, [FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 365);
        limit = Math.Clamp(limit, 1, 50);

        var counts = await _analytics.GetTopPostsAsync(days, limit, cancellationToken);

        if (counts is null || counts.Count == 0)
        {
            return Ok(new { enabled = counts is not null, days, posts = Array.Empty<object>() });
        }

        var titles = await TitlesFor(counts.Select(c => c.PostId).ToList(), cancellationToken);

        return Ok(new
        {
            enabled = true,
            days,
            posts = counts.Select(c => new
            {
                postId = c.PostId,
                title = titles.GetValueOrDefault(c.PostId, "(deleted)"),
                views = c.Views,
                uniqueVisitors = c.UniqueVisitors
            })
        });
    }

    // Views, unique visitors and claps per minute, hour or day, from the ClickHouse rollups.
    [HttpGet("posts/{id}/series")]
    public async Task<IActionResult> Series(
        int id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string bucket = "hour",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<SeriesGranularity>(bucket, ignoreCase: true, out var granularity))
        {
            return BadRequest("bucket must be minute, hour or day.");
        }

        var end = (to ?? DateTime.UtcNow).ToUniversalTime();
        var start = (from ?? end - DefaultRange(granularity)).ToUniversalTime();

        if (start >= end || end - start > MaxRange(granularity))
        {
            return BadRequest(
                $"from must be before to, and a {bucket} series spans at most {MaxRange(granularity).TotalDays:0} day(s).");
        }

        var points = await _analytics.GetSeriesAsync(id, start, end, granularity, cancellationToken);

        return Ok(new
        {
            postId = id,
            bucket = granularity.ToString().ToLowerInvariant(),
            from = start,
            to = end,
            enabled = points is not null,
            points = points ?? Array.Empty<SeriesPoint>()
        });
    }

    // Top posts over the last N minutes, exact to the minute.
    [HttpGet("top/window")]
    [Authorize(Roles = "Admin,Writer")]
    public async Task<IActionResult> TopInWindow(
        [FromQuery] int minutes = 60,
        [FromQuery] string by = "views",
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<WindowRanking>(by, ignoreCase: true, out var ranking))
        {
            return BadRequest("by must be views or claps.");
        }

        minutes = Math.Clamp(minutes, 1, 7 * 24 * 60);
        limit = Math.Clamp(limit, 1, 50);

        var entries = await _analytics.GetTopInWindowAsync(minutes, ranking, limit, cancellationToken);

        if (entries is null || entries.Count == 0)
        {
            return Ok(new { enabled = entries is not null, minutes, by = ranking.ToString().ToLowerInvariant(), posts = Array.Empty<object>() });
        }

        var titles = await TitlesFor(entries.Select(e => e.PostId).ToList(), cancellationToken);

        return Ok(new
        {
            enabled = true,
            minutes,
            by = ranking.ToString().ToLowerInvariant(),
            posts = entries.Select(e => new
            {
                postId = e.PostId,
                title = titles.GetValueOrDefault(e.PostId, "(deleted)"),
                views = e.Views,
                uniqueVisitors = e.UniqueVisitors,
                claps = e.Claps
            })
        });
    }

    // Views -> unique visitors -> clappers -> claps for one post, joining the two event streams.
    [HttpGet("posts/{id}/funnel")]
    [Authorize(Roles = "Admin,Writer")]
    public async Task<IActionResult> Funnel(int id, [FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 180);

        var funnel = await _analytics.GetEngagementAsync(id, days, cancellationToken);

        var visitors = funnel?.UniqueVisitors ?? 0;
        var clappers = funnel?.Clappers ?? 0;

        return Ok(new
        {
            postId = id,
            days,
            enabled = funnel is not null,
            views = funnel?.Views ?? 0,
            uniqueVisitors = visitors,
            clappers,
            claps = funnel?.Claps ?? 0,
            clapperRate = visitors == 0 ? 0 : Math.Round((double)clappers / visitors, 4)
        });
    }

    private static TimeSpan DefaultRange(SeriesGranularity granularity) => granularity switch
    {
        SeriesGranularity.Minute => TimeSpan.FromHours(1),
        SeriesGranularity.Hour => TimeSpan.FromDays(1),
        _ => TimeSpan.FromDays(30)
    };

    private static TimeSpan MaxRange(SeriesGranularity granularity) => granularity switch
    {
        SeriesGranularity.Minute => TimeSpan.FromDays(1),
        SeriesGranularity.Hour => TimeSpan.FromDays(31),
        _ => TimeSpan.FromDays(366)
    };

    [HttpGet("trending/stream")]
    public async Task TrendingStream(
        [FromQuery] int windowHours = 24,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        windowHours = Math.Clamp(windowHours, 1, 48);
        limit = Math.Clamp(limit, 1, 50);

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        Response.Headers["X-Accel-Buffering"] = "no";

        if (!_analytics.IsConfigured)
        {
            await WriteEventAsync("disabled", "{}", cancellationToken);

            return;
        }

        await foreach (var entries in _analytics
            .StreamTrendingAsync(windowHours, limit, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            var titles = await TitlesFor(entries.Select(e => e.PostId).ToList(), cancellationToken);

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                windowHours,
                generatedAt = DateTime.UtcNow,
                posts = entries.Select(e => new
                {
                    postId = e.PostId,
                    title = titles.GetValueOrDefault(e.PostId, "(deleted)"),
                    score = e.Score
                })
            });

            await WriteEventAsync("trending", payload, cancellationToken);
        }
    }

    private async Task WriteEventAsync(string name, string json, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {name}\ndata: {json}\n\n", cancellationToken);

        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task<Dictionary<int, string>> TitlesFor(
        IReadOnlyList<int> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await _context.BlogPosts
            .AsNoTracking()
            .Where(p => postIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title })
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);
    }
}
