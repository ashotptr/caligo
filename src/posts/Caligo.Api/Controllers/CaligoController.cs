using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Catalog;
using Caligo.Api.Dtos;
using Caligo.Catalog.Application.Engine;
using Caligo.SharedKernel;

namespace Caligo.Api.Controllers;

[Route("api/caligo")]
[ApiController]
[RequireCatalog]
[EnableRateLimiting("caligo")]
public class CaligoController : ApiControllerBase
{
    private readonly CaligoService _caligo;

    public CaligoController(CaligoService caligo)
    {
        _caligo = caligo;
    }

    [HttpPost("ingest")]
    [Authorize]
    public async Task<IActionResult> Ingest(IngestRequest request, CancellationToken cancellationToken) =>
        await StreamAsync(_caligo.Ingest(request.Query, CurrentCaller()), cancellationToken);

    [HttpPost("radio-queue")]
    public async Task<IActionResult> QueueToRadio(QueueTrackRequest request, CancellationToken cancellationToken) =>
        Respond(await _caligo.QueueAsync(new QueueTrackCommand(request.ContentId), CurrentCaller(), cancellationToken));

    [HttpPost("ask")]
    public async Task<IActionResult> Ask(AskRequest request, CancellationToken cancellationToken) =>
        Respond(await _caligo.AskAsync(new AskCommand(request.Query, request.SessionId), CurrentCaller(), cancellationToken));

    [HttpPost("rotation")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Rotation(RotationJobRequest request, CancellationToken cancellationToken) =>
        await StreamAsync(
            await _caligo.RotationAsync(
                new RotationCommand(request.AlbumId, request.SingleId, request.ContentId), CurrentCaller(), cancellationToken),
            cancellationToken);

    private async Task<IActionResult> StreamAsync(Result<IAsyncEnumerable<string>> result, CancellationToken cancellationToken)
    {
        if (result.Status != ResultStatus.Ok || result.Value is null)
        {
            return Failure(result.Status, result.Error);
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await Response.Body.FlushAsync(cancellationToken);

        await foreach (var line in result.Value.WithCancellation(cancellationToken))
        {
            await Response.WriteAsync("data: " + line.Replace("\r", " ").Replace("\n", " ") + "\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        await Response.WriteAsync("event: end\ndata: done\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);

        return new EmptyResult();
    }
}
