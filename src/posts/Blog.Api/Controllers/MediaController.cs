using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Blog.Api.Catalog;
using Blog.Api.Dtos;
using Blog.Catalog.Application.Media;

namespace Blog.Api.Controllers;

[ApiController]
[RequireCatalog]
[RequireMedia]
[EnableRateLimiting("reads")]
public class MediaController : ApiControllerBase
{
    private readonly MediaService _media;

    public MediaController(MediaService media)
    {
        _media = media;
    }

    [HttpGet("api/content/{id:int}/media")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        Respond(await _media.GetAsync(id, CurrentCaller(), cancellationToken));

    [HttpPost("api/content/{id:int}/media")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Queue(int id, MediaRequest request, CancellationToken cancellationToken) =>
        Respond(await _media.QueueAsync(id, request.SourceFile, CurrentCaller(), cancellationToken));

    [HttpGet("api/media/inbox")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetInbox(CancellationToken cancellationToken) =>
        Respond(await _media.GetInboxAsync(CurrentCaller(), cancellationToken));
}
