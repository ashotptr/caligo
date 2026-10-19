using Grpc.Core;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Infrastructure;
using Caligo.Contracts.Analytics;
using Caligo.Posts.Infrastructure.Messaging;

namespace Caligo.Api.Media;

public record PlaybackTitleDto(
    int ContentId,
    string Title,
    long Sessions,
    long Startups,
    double MedianStartupMs,
    double P95StartupMs,
    long Stalls,
    long StallMs,
    long QualitySwitches,
    long Errors,
    long HlsStartups,
    long NativeHlsStartups,
    long DashStartups,
    long ProgressiveStartups);

public record PlaybackQualityDto(bool Enabled, int Days, IReadOnlyList<PlaybackTitleDto> Titles);

public class PlaybackQuality
{
    public const int DefaultDays = 7;
    public const int MaxLimit = 50;

    private readonly AnalyticsReader.AnalyticsReaderClient _analytics;
    private readonly AnalyticsClientOptions _options;
    private readonly ICatalogReader _catalog;
    private readonly CatalogOptions _catalogOptions;
    private readonly ILogger<PlaybackQuality> _logger;

    public PlaybackQuality(
        AnalyticsReader.AnalyticsReaderClient analytics,
        IOptions<AnalyticsClientOptions> options,
        ICatalogReader catalog,
        IOptions<CatalogOptions> catalogOptions,
        ILogger<PlaybackQuality> logger)
    {
        _analytics = analytics;
        _options = options.Value;
        _catalog = catalog;
        _catalogOptions = catalogOptions.Value;
        _logger = logger;
    }

    public async Task<PlaybackQualityDto> SummaryAsync(int days, int limit, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 180);

        if (!_options.Enabled)
        {
            return new PlaybackQualityDto(false, days, Array.Empty<PlaybackTitleDto>());
        }

        PlaybackSummary reply;

        try
        {
            reply = await _analytics.GetPlaybackSummaryAsync(
                new GetPlaybackSummaryRequest { Days = days, Limit = Math.Clamp(limit, 1, MaxLimit) },
                new CallOptions(deadline: DateTime.UtcNow.Add(_options.OlapDeadline), cancellationToken: cancellationToken));
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Playback analytics are unavailable.");

            return new PlaybackQualityDto(false, days, Array.Empty<PlaybackTitleDto>());
        }

        var titles = _catalogOptions.Enabled && reply.Titles.Count > 0
            ? await _catalog.GetTitlesAsync(reply.Titles.Select(t => t.ContentId).ToList(), cancellationToken)
            : new Dictionary<int, string>();

        return new PlaybackQualityDto(
            reply.Enabled,
            reply.Days,
            reply.Titles.Select(t => new PlaybackTitleDto(
                t.ContentId,
                titles.TryGetValue(t.ContentId, out var title) ? title : $"Title {t.ContentId}",
                t.Sessions,
                t.Startups,
                t.MedianStartupMs,
                t.P95StartupMs,
                t.Stalls,
                t.StallMs,
                t.QualitySwitches,
                t.Errors,
                t.HlsStartups,
                t.NativeHlsStartups,
                t.DashStartups,
                t.ProgressiveStartups)).ToList());
    }
}
