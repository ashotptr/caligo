using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Dtos;
using Caligo.Api.MachineLearning;

namespace Caligo.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("reads")]
public class MachineLearningController : ControllerBase
{
    private readonly MachineLearningClient _ml;

    public MachineLearningController(MachineLearningClient ml)
    {
        _ml = ml;
    }

    [HttpGet("/api/insights/ml")]
    public Task<IActionResult> GetMonitoring(CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Get, "monitoring", null, cancellationToken);

    [HttpGet("/api/moderation/queue")]
    public Task<IActionResult> GetQueue(CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Get, "moderation/queue", null, cancellationToken);

    [HttpPost("/api/moderation/{commentId:int}/label")]
    public Task<IActionResult> LabelComment(int commentId, CommentLabelRequest request, CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Post, $"moderation/comments/{commentId}/label", new { request.Label }, cancellationToken);

    [HttpPost("/api/songs/genre")]
    public Task<IActionResult> LabelGenre(GenreLabelRequest request, CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Post, "songs/label", new { request.Uri, request.Genre }, cancellationToken);

    private async Task<IActionResult> ForwardAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (!_ml.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Song and comment analysis is not set up.");
        }

        try
        {
            var (status, content) = await _ml.SendAsync(method, path, body, cancellationToken);

            return new ContentResult { StatusCode = status, Content = content, ContentType = "application/json" };
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Song and comment analysis is not reachable.");
        }
    }
}
