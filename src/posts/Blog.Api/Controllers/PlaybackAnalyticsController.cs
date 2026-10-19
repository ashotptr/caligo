using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Blog.Api.Media;

namespace Blog.Api.Controllers;

[Route("api/analytics/playback")]
[ApiController]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("reads")]
public class PlaybackAnalyticsController : ControllerBase
{
    private readonly PlaybackQuality _quality;

    public PlaybackAnalyticsController(PlaybackQuality quality)
    {
        _quality = quality;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int days = PlaybackQuality.DefaultDays,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await _quality.SummaryAsync(days, limit, cancellationToken));
}
