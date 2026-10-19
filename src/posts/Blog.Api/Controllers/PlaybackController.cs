using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Blog.Api.Catalog;
using Blog.Api.Dtos;
using Blog.Catalog.Application.Media;
using Blog.SharedKernel;

namespace Blog.Api.Controllers;

[Route("api/v1/sessions")]
[ApiController]
[Authorize]
[RequireCatalog]
[RequireMedia]
[EnableRateLimiting("reads")]
public class PlaybackController : ApiControllerBase
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly PlaybackService _playback;

    public PlaybackController(PlaybackService playback)
    {
        _playback = playback;
    }

    [HttpPost]
    public async Task<IActionResult> Start(StartSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentId is not { } contentId)
        {
            return BadRequest("content_id is required.");
        }

        return Reply(await _playback.StartAsync(
            new StartSessionCommand(contentId, request.DeviceId), CurrentCaller(), cancellationToken));
    }

    [HttpPut("{id:guid}/position")]
    public async Task<IActionResult> ReportPosition(Guid id, PositionRequest request, CancellationToken cancellationToken)
    {
        if (request.Position is not { } position || request.Seq is not { } seq)
        {
            return BadRequest("position and seq are required.");
        }

        return Reply(await _playback.ReportPositionAsync(
            id, new PositionCommand(position, seq), CurrentCaller(), cancellationToken));
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CompleteRequest request, CancellationToken cancellationToken)
    {
        if (request.CompletionId is not { } completionId)
        {
            return BadRequest("completion_id is required.");
        }

        return Reply(await _playback.CompleteAsync(
            id, new CompleteCommand(completionId, request.Position), CurrentCaller(), cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> End(Guid id, CancellationToken cancellationToken) =>
        Respond(await _playback.EndAsync(id, CurrentCaller(), cancellationToken));

    [HttpPost("{id:guid}/events")]
    public async Task<IActionResult> RecordEvents(Guid id, PlaybackEventsRequest request, CancellationToken cancellationToken)
    {
        var events = request.Events?
            .Select(e => new PlaybackEventCommand(
                e.EventId, e.Seq, e.Type, e.At, e.Position, e.DurationMs, e.From, e.To, e.Delivery, e.Detail))
            .ToList();

        return Reply(await _playback.RecordEventsAsync(id, events, CurrentCaller(), cancellationToken));
    }

    private IActionResult Reply<T>(Result<T> result) =>
        result.IsSuccess
            ? new JsonResult(result.Value, Wire)
            : Failure(result.Status, result.Error);
}
