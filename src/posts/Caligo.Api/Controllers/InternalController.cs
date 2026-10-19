using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Caligo.Api.Catalog;
using Caligo.Api.Dtos;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Media;
using Caligo.SharedKernel;

namespace Caligo.Api.Controllers;

[Route("internal/v1")]
[ApiController]
[AllowAnonymous]
[RequireInternalKey]
[RequireCatalog]
[ApiExplorerSettings(IgnoreApi = true)]
public class InternalController : ApiControllerBase
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly PlaybackService _playback;
    private readonly PackagingService _packaging;

    public InternalController(PlaybackService playback, PackagingService packaging)
    {
        _playback = playback;
        _packaging = packaging;
    }

    [HttpGet("media/sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken cancellationToken)
    {
        var access = await _playback.GetAccessAsync(id, cancellationToken);

        return access.IsSuccess ? new JsonResult(access.Value, Wire) : Failure(access.Status, access.Error);
    }

    [HttpPost("packaging/claim")]
    public async Task<IActionResult> Claim(CancellationToken cancellationToken)
    {
        var job = await _packaging.ClaimAsync(cancellationToken);

        return job is null ? NoContent() : new JsonResult(job, Wire);
    }

    [HttpPost("packaging/{contentId:int}/extend")]
    public async Task<IActionResult> Extend(int contentId, PackagingClaimRequest request, CancellationToken cancellationToken) =>
        Respond(await _packaging.ExtendAsync(contentId, ClaimOf(request), cancellationToken));

    [HttpPost("packaging/{contentId:int}/done")]
    public async Task<IActionResult> Done(int contentId, PackagingReportRequest request, CancellationToken cancellationToken)
    {
        var report = new PackagingReport(
            request.Attempts,
            request.SourceFile ?? string.Empty,
            request.StoragePath ?? string.Empty,
            request.HlsPath,
            request.DashPath,
            request.DurationSeconds,
            (request.Renditions ?? new List<PackagedRenditionRequest>())
                .Select(r => new PackagedRendition(
                    r.Kind ?? string.Empty, r.Codec ?? string.Empty, r.Bitrate, r.Width, r.Height, r.Language, r.Path ?? string.Empty))
                .ToList(),
            (request.Subtitles ?? new List<PackagedSubtitleRequest>())
                .Select(s => new PackagedSubtitle(s.LanguageId, s.Uri ?? string.Empty))
                .ToList());

        return Respond(await _packaging.CompleteAsync(contentId, report, cancellationToken));
    }

    [HttpPost("packaging/{contentId:int}/failed")]
    public async Task<IActionResult> Failed(int contentId, PackagingFailureRequest request, CancellationToken cancellationToken) =>
        Respond(await _packaging.FailAsync(contentId, ClaimOf(request), request.Error, cancellationToken));

    [HttpPost("packaging/{contentId:int}/release")]
    public async Task<IActionResult> Release(int contentId, PackagingClaimRequest request, CancellationToken cancellationToken) =>
        Respond(await _packaging.ReleaseAsync(contentId, ClaimOf(request), cancellationToken));

    private static PackagingClaimRef ClaimOf(PackagingClaimRequest request) =>
        new(request.Attempts, request.SourceFile ?? string.Empty);
}
