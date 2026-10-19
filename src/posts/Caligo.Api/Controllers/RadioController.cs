using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Radio;

namespace Caligo.Api.Controllers;

[Route("api/radio")]
[ApiController]
[EnableRateLimiting("reads")]
public class RadioController : ControllerBase
{
    private readonly RadioHistory _history;

    public RadioController(RadioHistory history)
    {
        _history = history;
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent(
        [FromQuery] int limit = RadioHistory.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var plays = await _history.RecentAsync(limit, cancellationToken);

        return Ok(new
        {
            enabled = plays is not null,
            plays = plays ?? Array.Empty<RadioPlayDto>()
        });
    }
}
