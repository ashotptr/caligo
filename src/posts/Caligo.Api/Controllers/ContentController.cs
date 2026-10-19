using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Catalog;
using Caligo.Api.Dtos;
using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;

namespace Caligo.Api.Controllers;

[Route("api/content")]
[ApiController]
[RequireCatalog]
[EnableRateLimiting("reads")]
public class ContentController : ApiControllerBase
{
    private readonly CatalogService _catalog;
    private readonly ActivityService _activity;

    public ContentController(CatalogService catalog, ActivityService activity)
    {
        _catalog = catalog;
        _activity = activity;
    }

    [HttpGet]
    public async Task<IActionResult> GetPage(
        [FromQuery] string? kind,
        [FromQuery] int? genreId,
        [FromQuery] string? query,
        [FromQuery] string? cursor,
        [FromQuery] int limit = CatalogService.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Respond(await _catalog.GetPageAsync(kind, genreId, query, cursor, limit, cancellationToken));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetAsync(id, cancellationToken));

    [HttpGet("{id:int}/similar")]
    public async Task<IActionResult> GetSimilar(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetSimilarAsync(id, cancellationToken));

    [HttpGet("{id:int}/comments")]
    public async Task<IActionResult> GetComments(
        int id,
        [FromQuery] int? before,
        [FromQuery] int limit = CatalogService.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Respond(await _catalog.GetCommentsAsync(id, before, limit, cancellationToken));

    [HttpPost("{id:int}/comments")]
    [Authorize]
    public async Task<IActionResult> AddComment(int id, CommentRequest request, CancellationToken cancellationToken) =>
        Respond(await _activity.AddCommentAsync(id, request.Comment, CurrentCaller(), cancellationToken));

    [HttpGet("{id:int}/interaction")]
    [Authorize]
    public async Task<IActionResult> GetInteraction(int id, CancellationToken cancellationToken) =>
        Respond(await _activity.GetInteractionAsync(id, CurrentCaller(), cancellationToken));

    [HttpPut("{id:int}/interaction")]
    [Authorize]
    public async Task<IActionResult> SetInteraction(int id, InteractionRequest request, CancellationToken cancellationToken) =>
        Respond(await _activity.SetInteractionAsync(
            id, new SetInteractionCommand(request.Liked, request.Rating), CurrentCaller(), cancellationToken));

    [HttpDelete("{id:int}/interaction")]
    [Authorize]
    public async Task<IActionResult> RemoveInteraction(int id, CancellationToken cancellationToken) =>
        Respond(await _activity.RemoveInteractionAsync(id, CurrentCaller(), cancellationToken));

    [HttpPost("{id:int}/watch")]
    [Authorize]
    public async Task<IActionResult> Watch(int id, WatchRequest request, CancellationToken cancellationToken) =>
        Respond(await _activity.LogWatchAsync(id, new WatchCommand(request.Timestamp), CurrentCaller(), cancellationToken));
}
