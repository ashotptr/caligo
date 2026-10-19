using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Blog.Api.Catalog;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Contents;

namespace Blog.Api.Controllers;

[Route("api")]
[ApiController]
[RequireCatalog]
[EnableRateLimiting("reads")]
public class CatalogController : ApiControllerBase
{
    private readonly CatalogService _catalog;
    private readonly StreamingAccountService _accounts;

    public CatalogController(CatalogService catalog, StreamingAccountService accounts)
    {
        _catalog = catalog;
        _accounts = accounts;
    }

    [HttpGet("series")]
    public async Task<IActionResult> GetSeriesList(CancellationToken cancellationToken) =>
        Ok(await _catalog.GetSeriesListAsync(cancellationToken));

    [HttpGet("series/{id:int}")]
    public async Task<IActionResult> GetSeries(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetSeriesAsync(id, cancellationToken));

    [HttpGet("actors/{id:int}")]
    public async Task<IActionResult> GetActor(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetActorAsync(id, cancellationToken));

    [HttpGet("artists")]
    public async Task<IActionResult> GetArtists(
        [FromQuery] string? query,
        [FromQuery] int limit = CatalogService.MaxPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await _catalog.GetArtistsAsync(query, limit, cancellationToken));

    [HttpGet("artists/{id:int}")]
    public async Task<IActionResult> GetArtist(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetArtistAsync(id, cancellationToken));

    [HttpGet("albums")]
    public async Task<IActionResult> GetAlbums(
        [FromQuery] string? query,
        [FromQuery] int limit = CatalogService.MaxPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await _catalog.GetAlbumsAsync(query, limit, cancellationToken));

    [HttpGet("albums/{id:int}")]
    public async Task<IActionResult> GetAlbum(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetAlbumAsync(id, cancellationToken));

    [HttpGet("singles/{id:int}")]
    public async Task<IActionResult> GetSingle(int id, CancellationToken cancellationToken) =>
        Respond(await _catalog.GetSingleAsync(id, cancellationToken));

    [HttpGet("songs/lookup")]
    public async Task<IActionResult> FindSong(
        [FromQuery] string? artist, [FromQuery] string? title, CancellationToken cancellationToken) =>
        Respond(await _catalog.FindSongAsync(artist, title, cancellationToken));

    [HttpGet("genres")]
    public async Task<IActionResult> GetGenres(CancellationToken cancellationToken) =>
        Ok(await _catalog.GetGenresAsync(cancellationToken));

    [HttpGet("languages")]
    public async Task<IActionResult> GetLanguages(CancellationToken cancellationToken) =>
        Ok(await _catalog.GetLanguagesAsync(cancellationToken));

    [HttpGet("subscription/plans")]
    public async Task<IActionResult> GetPlans(CancellationToken cancellationToken) =>
        Ok(await _accounts.GetPlansAsync(cancellationToken));
}
