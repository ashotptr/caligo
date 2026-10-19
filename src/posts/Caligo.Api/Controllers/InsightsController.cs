using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Catalog;
using Caligo.Catalog.Application.Insights;
using Caligo.Catalog.Application.Music;

namespace Caligo.Api.Controllers;

[Route("api/insights")]
[ApiController]
[RequireCatalog]
[EnableRateLimiting("reads")]
public class InsightsController : ApiControllerBase
{
    private readonly InsightsService _insights;
    private readonly MusicImportService _music;

    public InsightsController(InsightsService insights, MusicImportService music)
    {
        _insights = insights;
        _music = music;
    }

    [HttpGet("popular")]
    public async Task<IActionResult> GetPopular(CancellationToken cancellationToken) =>
        Ok(await _insights.GetPopularAsync(cancellationToken));

    [HttpGet("engaging")]
    public async Task<IActionResult> GetEngaging(CancellationToken cancellationToken) =>
        Ok(await _insights.GetEngagingAsync(cancellationToken));

    [HttpGet("active-users")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetActiveUsers(CancellationToken cancellationToken) =>
        Ok(await _insights.GetActiveUsersAsync(cancellationToken));

    [HttpGet("queries")]
    [Authorize(Roles = "Admin")]
    public IActionResult GetQueries() => Ok(_insights.Queries);

    [HttpGet("queries/{number:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RunQuery(int number, CancellationToken cancellationToken) =>
        Respond(await _insights.RunQueryAsync(number, cancellationToken));

    [HttpPost("/api/music/import")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ImportMusic(CancellationToken cancellationToken)
    {
        if (!_music.IsConfigured)
        {
            return Conflict("Set Catalog:CaligoPublicRoot to Caligo's public_html folder to import music.");
        }

        return Ok(await _music.ImportAsync(cancellationToken));
    }
}
