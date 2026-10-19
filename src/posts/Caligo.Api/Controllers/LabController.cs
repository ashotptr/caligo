using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Lab;

namespace Caligo.Api.Controllers;

[Route("api/lab")]
[ApiController]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("reads")]
public class LabController : ApiControllerBase
{
    private readonly LabResults _lab;

    public LabController(LabResults lab)
    {
        _lab = lab;
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] int limit = LabResults.DefaultLimit, CancellationToken cancellationToken = default) =>
        Respond(await _lab.RunsAsync(limit, cancellationToken));

    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> Run(Guid id, CancellationToken cancellationToken = default) =>
        Respond(await _lab.RunAsync(id, cancellationToken));
}
