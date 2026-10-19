using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Media;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Media;

public class MediaService
{
    public const string InboxMissing = "Set Media:IncomingPath to the folder that holds source videos.";

    private readonly ICatalogReader _catalog;
    private readonly IMediaStore _media;
    private readonly IMediaInbox _inbox;
    private readonly TimeProvider _time;

    public MediaService(ICatalogReader catalog, IMediaStore media, IMediaInbox inbox, TimeProvider time)
    {
        _catalog = catalog;
        _media = media;
        _inbox = inbox;
        _time = time;
    }

    public async Task<Result<MediaInfoDto>> GetAsync(
        int contentId, Caller caller, CancellationToken cancellationToken = default)
    {
        var media = await _media.FindAsync(contentId, cancellationToken);

        if (media is null)
        {
            return Result<MediaInfoDto>.NotFound();
        }

        return Result<MediaInfoDto>.Ok(await DescribeAsync(media, caller.IsAdministrator, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<InboxFileDto>>> GetInboxAsync(
        Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdministrator)
        {
            return Result<IReadOnlyList<InboxFileDto>>.Forbidden();
        }

        if (!_inbox.IsConfigured)
        {
            return Result<IReadOnlyList<InboxFileDto>>.Conflict(InboxMissing);
        }

        var entries = _inbox.List();
        var names = entries.Select(e => e.File).ToList();
        var languages = (await _catalog.GetLanguagesAsync(cancellationToken))
            .Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = entries
            .Where(e => MediaPaths.IsSourceFile(e.File))
            .Select(e => new InboxFileDto(
                e.File,
                e.Size,
                e.Modified,
                Sidecars.Of(e.File, names)
                    .Select(s => new InboxSubtitleDto(s.File, s.Tag, languages.Contains(s.Tag)))
                    .ToList()))
            .ToList();

        return Result<IReadOnlyList<InboxFileDto>>.Ok(files);
    }

    public async Task<Result<MediaInfoDto>> QueueAsync(
        int contentId, string? sourceFile, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdministrator)
        {
            return Result<MediaInfoDto>.Forbidden();
        }

        if (!MediaPaths.IsSourceFile(sourceFile))
        {
            return Result<MediaInfoDto>.Invalid("Name a video file from the incoming folder.");
        }

        if (!_inbox.IsConfigured)
        {
            return Result<MediaInfoDto>.Conflict(InboxMissing);
        }

        if (!await _catalog.ContentExistsAsync(contentId, cancellationToken))
        {
            return Result<MediaInfoDto>.NotFound();
        }

        if (!_inbox.Contains(sourceFile!))
        {
            return Result<MediaInfoDto>.Invalid("That file is not in the incoming folder.");
        }

        var now = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
        var queued = await _media.QueueAsync(contentId, sourceFile!, now, cancellationToken);

        return queued is null
            ? Result<MediaInfoDto>.Conflict("This title is being packaged right now. Try again when it has finished.")
            : Result<MediaInfoDto>.Ok(await DescribeAsync(queued, true, cancellationToken));
    }

    private async Task<MediaInfoDto> DescribeAsync(
        ContentMedia media, bool administrator, CancellationToken cancellationToken)
    {
        var renditions = media.IsPlayable
            ? await _media.GetRenditionsAsync(media.ContentId, cancellationToken)
            : Array.Empty<Rendition>();

        var subtitles = media.IsPlayable
            ? await _media.GetSubtitlesAsync(media.ContentId, cancellationToken)
            : Array.Empty<SubtitleTrack>();

        var formats = new List<string>();

        if (media.IsPlayable && media.HlsPath is not null)
        {
            formats.Add(MediaFormats.Hls);
        }

        if (media.IsPlayable && media.DashPath is not null)
        {
            formats.Add(MediaFormats.Dash);
        }

        if (renditions.Any(r => r.Kind == RenditionKinds.Progressive))
        {
            formats.Add(MediaFormats.Progressive);
        }

        return new MediaInfoDto(
            media.ContentId,
            media.Status,
            media.IsPlayable,
            media.IsPlayable ? media.DurationSeconds : null,
            media.PackagedAt,
            formats,
            renditions
                .OrderBy(r => r.Kind == RenditionKinds.Video ? 0 : r.Kind == RenditionKinds.Audio ? 1 : 2)
                .ThenByDescending(r => r.Bitrate)
                .Select(ToDto)
                .ToList(),
            subtitles.Select(s => new SubtitleDto(s.Language, LanguageTags.For(s.Language))).ToList(),
            administrator ? media.SourceFile : null,
            administrator ? media.Error : null);
    }

    internal static RenditionDto ToDto(Rendition rendition) => new(
        rendition.Kind, rendition.Codec, rendition.Bitrate, rendition.Width, rendition.Height, rendition.Language);
}
