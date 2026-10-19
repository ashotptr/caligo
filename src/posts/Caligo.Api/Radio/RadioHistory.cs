using Grpc.Core;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Infrastructure;
using Caligo.Contracts.Analytics;
using Caligo.Posts.Infrastructure.Messaging;

namespace Caligo.Api.Radio;

public record RadioPlayDto(DateTime StartedAt, string Artist, string Album, string Title, bool IsRequest, int? ContentId);

public class RadioHistory
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    private readonly AnalyticsReader.AnalyticsReaderClient _analytics;
    private readonly AnalyticsClientOptions _options;
    private readonly CatalogService _catalog;
    private readonly CatalogOptions _catalogOptions;
    private readonly ILogger<RadioHistory> _logger;

    public RadioHistory(
        AnalyticsReader.AnalyticsReaderClient analytics,
        IOptions<AnalyticsClientOptions> options,
        CatalogService catalog,
        IOptions<CatalogOptions> catalogOptions,
        ILogger<RadioHistory> logger)
    {
        _analytics = analytics;
        _options = options.Value;
        _catalog = catalog;
        _catalogOptions = catalogOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RadioPlayDto>?> RecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        RadioPlays reply;

        try
        {
            reply = await _analytics.GetRadioPlaysAsync(
                new GetRadioPlaysRequest { Limit = Math.Clamp(limit, 1, MaxLimit) },
                new CallOptions(deadline: DateTime.UtcNow.Add(_options.OlapDeadline), cancellationToken: cancellationToken));
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Radio history is unavailable.");

            return null;
        }

        if (!reply.Enabled)
        {
            return null;
        }

        var songs = new Dictionary<(string Artist, string Title), int?>();
        var plays = new List<RadioPlayDto>(reply.Plays.Count);

        foreach (var play in reply.Plays)
        {
            var key = (play.Artist, play.Title);

            if (!songs.TryGetValue(key, out var contentId))
            {
                contentId = await FindSongAsync(play.Artist, play.Title, cancellationToken);

                songs[key] = contentId;
            }

            plays.Add(new RadioPlayDto(
                play.StartedAt.ToDateTime(), play.Artist, play.Album, play.Title, play.IsRequest, contentId));
        }

        return plays;
    }

    private async Task<int?> FindSongAsync(string artist, string title, CancellationToken cancellationToken)
    {
        if (!_catalogOptions.Enabled)
        {
            return null;
        }

        var match = await _catalog.FindSongAsync(artist, title, cancellationToken);

        return match.IsSuccess ? match.Value!.ContentId : null;
    }
}
