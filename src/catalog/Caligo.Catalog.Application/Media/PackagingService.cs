using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Media;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Media;

public record PackagingSubtitleDto(string File, int LanguageId, string Language, string? Tag);

public record PackagingJobDto(
    int ContentId,
    string SourceFile,
    int Attempts,
    string? StoragePath,
    int ClaimSeconds,
    IReadOnlyList<PackagingSubtitleDto> Subtitles);

public record PackagingClaimRef(int Attempts, string SourceFile);

public record PackagingReport(
    int Attempts,
    string SourceFile,
    string StoragePath,
    string? HlsPath,
    string? DashPath,
    float DurationSeconds,
    IReadOnlyList<PackagedRendition> Renditions,
    IReadOnlyList<PackagedSubtitle> Subtitles);

public class PackagingService
{
    public const int MaxRenditions = 32;
    public const int MaxSubtitles = 64;
    public const int MaxCodecLength = 100;
    public const int MaxLanguageLength = 50;
    public const int MaxDimension = 10_000;
    public const string ClaimLost = "This packaging job is no longer held by this packager.";

    private readonly IPackagingQueue _queue;
    private readonly IMediaInbox _inbox;
    private readonly ICatalogReader _catalog;
    private readonly MediaRules _rules;
    private readonly TimeProvider _time;

    public PackagingService(
        IPackagingQueue queue, IMediaInbox inbox, ICatalogReader catalog, MediaRules rules, TimeProvider time)
    {
        _queue = queue;
        _inbox = inbox;
        _catalog = catalog;
        _rules = rules;
        _time = time;
    }

    public async Task<PackagingJobDto?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        var job = await _queue.ClaimAsync(
            now, now + _rules.PackagingClaim, Math.Max(_rules.MaxPackagingAttempts, 1), cancellationToken);

        if (job is null)
        {
            return null;
        }

        var subtitles = new List<PackagingSubtitleDto>();

        if (_inbox.IsConfigured)
        {
            var languages = new Dictionary<string, NamedDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var language in await _catalog.GetLanguagesAsync(cancellationToken))
            {
                languages.TryAdd(language.Name.Trim(), language);
            }

            foreach (var sidecar in Sidecars.Of(job.SourceFile, _inbox.List().Select(e => e.File)))
            {
                if (languages.TryGetValue(sidecar.Tag, out var language) &&
                    subtitles.All(s => s.LanguageId != language.Id))
                {
                    subtitles.Add(new PackagingSubtitleDto(
                        sidecar.File, language.Id, language.Name, LanguageTags.For(language.Name)));
                }
            }
        }

        return new PackagingJobDto(
            job.ContentId,
            job.SourceFile,
            job.Attempts,
            job.StoragePath,
            (int)Math.Max(_rules.PackagingClaim.TotalSeconds, 1),
            subtitles);
    }

    public async Task<Result> ExtendAsync(
        int contentId, PackagingClaimRef claim, CancellationToken cancellationToken = default) =>
        await _queue.ExtendAsync(Job(contentId, claim), Now() + _rules.PackagingClaim, cancellationToken)
            ? Result.Ok()
            : Result.Conflict(ClaimLost);

    public async Task<Result> CompleteAsync(
        int contentId, PackagingReport report, CancellationToken cancellationToken = default)
    {
        var problem = Validate(contentId, report);

        if (problem is not null)
        {
            return Result.Invalid(problem);
        }

        var result = new PackagingResult(
            contentId,
            report.StoragePath,
            report.HlsPath,
            report.DashPath,
            report.DurationSeconds,
            report.Renditions,
            report.Subtitles);

        return await _queue.PublishAsync(
            Job(contentId, new PackagingClaimRef(report.Attempts, report.SourceFile)), result, Now(), cancellationToken)
            ? Result.Ok()
            : Result.Conflict(ClaimLost);
    }

    public async Task<Result> FailAsync(
        int contentId, PackagingClaimRef claim, string? error, CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "The packager failed without saying why." : error.Trim();

        return await _queue.FailAsync(Job(contentId, claim), text, cancellationToken)
            ? Result.Ok()
            : Result.Conflict(ClaimLost);
    }

    public async Task<Result> ReleaseAsync(
        int contentId, PackagingClaimRef claim, CancellationToken cancellationToken = default) =>
        await _queue.ReleaseAsync(Job(contentId, claim), cancellationToken)
            ? Result.Ok()
            : Result.Conflict(ClaimLost);

    public static string? Validate(int contentId, PackagingReport report)
    {
        if (!MediaPaths.IsSafeRelative(report.StoragePath) ||
            !report.StoragePath.StartsWith($"{contentId}/", StringComparison.Ordinal))
        {
            return "storage_path must be a folder under the title's own folder.";
        }

        if (report.HlsPath is null && report.DashPath is null)
        {
            return "A package needs an HLS playlist or a DASH manifest.";
        }

        if (report.HlsPath is not null && !IsFile(report.HlsPath, ".m3u8"))
        {
            return "hls_path must name an .m3u8 file inside the package.";
        }

        if (report.DashPath is not null && !IsFile(report.DashPath, ".mpd"))
        {
            return "dash_path must name an .mpd file inside the package.";
        }

        if (!float.IsFinite(report.DurationSeconds) || report.DurationSeconds <= 0)
        {
            return "duration_seconds must be a positive number.";
        }

        if (report.Renditions is null || report.Renditions.Count == 0 || report.Renditions.Count > MaxRenditions)
        {
            return $"Send between 1 and {MaxRenditions} renditions.";
        }

        foreach (var rendition in report.Renditions)
        {
            if (rendition.Kind is not (RenditionKinds.Video or RenditionKinds.Audio or RenditionKinds.Progressive))
            {
                return "A rendition's kind is video, audio or progressive.";
            }

            if (string.IsNullOrWhiteSpace(rendition.Codec) || rendition.Codec.Length > MaxCodecLength ||
                rendition.Codec.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or ',' or '-' or '_')))
            {
                return "A rendition's codec is an RFC 6381 codecs string.";
            }

            if (rendition.Bitrate <= 0 ||
                rendition.Width is < 1 or > MaxDimension ||
                rendition.Height is < 1 or > MaxDimension)
            {
                return "A rendition needs a positive bitrate and a sensible size.";
            }

            if (rendition.Language is { Length: > MaxLanguageLength })
            {
                return $"A rendition's language is at most {MaxLanguageLength} characters.";
            }

            if (!MediaPaths.IsSafeRelative(rendition.Path) || MediaPaths.ContentType(rendition.Path) is null)
            {
                return "A rendition's path must name a media file inside the package.";
            }
        }

        if (report.Subtitles is null || report.Subtitles.Count > MaxSubtitles)
        {
            return $"Send at most {MaxSubtitles} subtitles.";
        }

        if (report.Subtitles.Any(s => s.LanguageId <= 0 || !IsFile(s.Uri, ".vtt")))
        {
            return "A subtitle needs a language_id and a .vtt file inside the package.";
        }

        return null;
    }

    private static bool IsFile(string path, string extension) =>
        MediaPaths.IsSafeRelative(path) && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static PackagingJob Job(int contentId, PackagingClaimRef claim) =>
        new(contentId, claim.SourceFile, claim.Attempts, null);

    private DateTime Now() => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
