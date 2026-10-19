using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Caligo.Contracts.Analytics;

using IViewStore = Caligo.Analytics.Core.Views.IViewStore;
using ITrendingStore = Caligo.Analytics.Core.Views.ITrendingStore;
using CoreStats = Caligo.Analytics.Core.Views.PostStats;
using IOlapQueries = Caligo.Analytics.Core.Olap.IOlapQueries;
using CoreBucket = Caligo.Analytics.Core.Olap.SeriesBucket;
using CoreRankBy = Caligo.Analytics.Core.Olap.RankBy;
using IRadioStore = Caligo.Analytics.Core.Radio.IRadioStore;
using IPlaybackStore = Caligo.Analytics.Core.Playback.IPlaybackStore;
using ILabStore = Caligo.Analytics.Core.Lab.ILabStore;
using CoreLabRun = Caligo.Analytics.Core.Lab.LabRunSummary;

namespace Caligo.Analytics.Host.Services;

public class AnalyticsReaderService : AnalyticsReader.AnalyticsReaderBase
{
    private const int MaxBatchSize = 100;
    private const int MaxRadioPlays = 100;
    private const int MaxPlaybackTitles = 100;
    private const int MaxLabRuns = 200;

    private static readonly TimeSpan MaxMinuteSeries = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxHourSeries = TimeSpan.FromDays(31);
    private static readonly TimeSpan MaxDaySeries = TimeSpan.FromDays(366);

    private readonly IViewStore _views;
    private readonly ITrendingStore _trending;
    private readonly IOlapQueries _olap;
    private readonly IRadioStore _radio;
    private readonly IPlaybackStore _playback;
    private readonly ILabStore _lab;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalyticsReaderService> _logger;

    public AnalyticsReaderService(
        IViewStore views,
        ITrendingStore trending,
        IOlapQueries olap,
        IRadioStore radio,
        IPlaybackStore playback,
        ILabStore lab,
        IConfiguration configuration,
        ILogger<AnalyticsReaderService> logger)
    {
        _views = views;
        _trending = trending;
        _olap = olap;
        _radio = radio;
        _playback = playback;
        _lab = lab;
        _configuration = configuration;
        _logger = logger;
    }

    public override async Task<RadioPlays> GetRadioPlays(GetRadioPlaysRequest request, ServerCallContext context)
    {
        var reply = new RadioPlays { Enabled = _radio.IsConfigured };

        if (!_radio.IsConfigured)
        {
            return reply;
        }

        var plays = await _radio.RecentPlaysAsync(
            Math.Clamp(request.Limit, 1, MaxRadioPlays), context.CancellationToken);

        reply.Plays.AddRange(plays.Select(play => new RadioPlay
        {
            StartedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(play.StartedAt, DateTimeKind.Utc)),
            Artist = play.Artist,
            Album = play.Album,
            Title = play.Title,
            IsRequest = play.IsRequest
        }));

        return reply;
    }

    public override async Task<LabRuns> GetLabRuns(GetLabRunsRequest request, ServerCallContext context)
    {
        var reply = new LabRuns { Enabled = _lab.IsConfigured };

        if (!_lab.IsConfigured)
        {
            return reply;
        }

        var runs = await _lab.RunsAsync(Math.Clamp(request.Limit == 0 ? 50 : request.Limit, 1, MaxLabRuns), context.CancellationToken);

        reply.Runs.AddRange(runs.Select(ToProto));

        return reply;
    }

    public override async Task<LabRunDetail> GetLabRun(GetLabRunRequest request, ServerCallContext context)
    {
        var reply = new LabRunDetail { Enabled = _lab.IsConfigured };

        if (!_lab.IsConfigured || !Guid.TryParse(request.RunId, out var runId))
        {
            return reply;
        }

        var detail = await _lab.RunAsync(runId, context.CancellationToken);

        if (detail is null)
        {
            return reply;
        }

        reply.Found = true;
        reply.Run = ToProto(detail.Run);
        reply.Contestants.AddRange(detail.Contestants.Select(result => new LabContestant
        {
            Contestant = result.Contestant,
            Server = result.Server,
            Protocol = result.Protocol,
            Container = result.Container,
            Repetitions = result.Repetitions,
            Listeners = result.Listeners,
            Connected = result.Connected,
            TtfbP50Ms = result.TtfbP50Ms,
            StartupP50Ms = result.StartupP50Ms,
            StartupP95Ms = result.StartupP95Ms,
            StartupSpreadMs = result.StartupSpreadMs,
            Stalls = result.Stalls,
            StallMs = result.StallMs,
            MediaMs = result.MediaMs,
            LatencyP50Ms = result.LatencyP50Ms,
            LatencySpreadMs = result.LatencySpreadMs,
            DataLatencyP50Ms = result.DataLatencyP50Ms,
            CpuMeanPercent = result.CpuMeanPercent,
            CpuPeakPercent = result.CpuPeakPercent,
            MemoryPeakBytes = result.MemoryPeakBytes,
            ImpairedPackets = result.ImpairedPackets,
            ImpairedLost = result.ImpairedLost,
            ImpairedOverflowed = result.ImpairedOverflowed,
            Errors = result.Errors
        }));
        reply.Latency.AddRange(detail.Latency.Select(point => new LabLatencyPoint
        {
            Contestant = point.Contestant,
            Repetition = point.Repetition,
            MeasuredAt = Timestamp.FromDateTime(DateTime.SpecifyKind(point.MeasuredAt, DateTimeKind.Utc)),
            LatencyMs = point.LatencyMs
        }));

        return reply;
    }

    private static LabRun ToProto(CoreLabRun run) => new()
    {
        RunId = run.RunId.ToString(),
        Scenario = run.Scenario,
        Profile = run.Profile,
        StartedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(run.StartedAt, DateTimeKind.Utc)),
        FinishedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(run.FinishedAt, DateTimeKind.Utc)),
        Listeners = run.Listeners,
        Seconds = run.Seconds,
        Repetitions = run.Repetitions,
        Contestants = run.Contestants,
        SourceBiasMs = run.SourceBiasMs,
        Notes = run.Notes
    };

    public override async Task<PlaybackSummary> GetPlaybackSummary(GetPlaybackSummaryRequest request, ServerCallContext context)
    {
        var days = Math.Clamp(request.Days == 0 ? 7 : request.Days, 1, 180);
        var reply = new PlaybackSummary { Enabled = _playback.IsConfigured, Days = days };

        if (!_playback.IsConfigured)
        {
            return reply;
        }

        var titles = await _playback.SummaryAsync(
            days, Math.Clamp(request.Limit == 0 ? 20 : request.Limit, 1, MaxPlaybackTitles), context.CancellationToken);

        reply.Titles.AddRange(titles.Select(title => new PlaybackTitle
        {
            ContentId = title.ContentId,
            Sessions = title.Sessions,
            Startups = title.Startups,
            MedianStartupMs = title.MedianStartupMs,
            P95StartupMs = title.P95StartupMs,
            Stalls = title.Stalls,
            StallMs = title.StallMs,
            QualitySwitches = title.QualitySwitches,
            Errors = title.Errors,
            HlsStartups = title.HlsStartups,
            NativeHlsStartups = title.NativeHlsStartups,
            DashStartups = title.DashStartups,
            ProgressiveStartups = title.ProgressiveStartups
        }));

        return reply;
    }

    public override async Task<PostStats> GetPostStats(GetPostStatsRequest request, ServerCallContext context)
    {
        CoreStats stats = await _views.ForPostAsync(request.PostId, context.CancellationToken);

        return ToProto(stats);
    }

    public override async Task<PostStatsBatch> GetPostStatsBatch(
        GetPostStatsBatchRequest request, ServerCallContext context)
    {
        if (request.PostIds.Count == 0)
        {
            return new PostStatsBatch();
        }

        if (request.PostIds.Count > MaxBatchSize)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, $"At most {MaxBatchSize} post ids per request."));
        }

        var stats = await _views.ForPostsAsync(request.PostIds.ToList(), context.CancellationToken);

        var response = new PostStatsBatch();
        response.Stats.AddRange(stats.Select(ToProto));

        return response;
    }

    public override async Task<TopPosts> GetTopPosts(GetTopPostsRequest request, ServerCallContext context)
    {
        var days = Math.Clamp(request.Days == 0 ? 7 : request.Days, 1, 365);
        var limit = Math.Clamp(request.Limit == 0 ? 10 : request.Limit, 1, 50);

        var stats = await _views.TopPostsAsync(days, limit, context.CancellationToken);

        var response = new TopPosts();
        response.Posts.AddRange(stats.Select(ToProto));

        return response;
    }

    public override async Task StreamTrending(
        StreamTrendingRequest request,
        IServerStreamWriter<TrendingSnapshot> responseStream,
        ServerCallContext context)
    {
        var limit = Math.Clamp(request.Limit == 0 ? 10 : request.Limit, 1, 100);
        var windowHours = Math.Clamp(request.WindowHours == 0 ? 24 : request.WindowHours, 1, 48);

        var interval = _configuration.GetValue("Analytics:TrendingPushInterval", TimeSpan.FromSeconds(2));

        _logger.LogInformation(
            "Trending stream opened ({Window}h window, top {Limit}).", windowHours, limit);

        try
        {
            var lastSent = string.Empty;

            while (!context.CancellationToken.IsCancellationRequested)
            {
                var entries = await _trending.TopAsync(windowHours, limit, context.CancellationToken);

                var snapshot = new TrendingSnapshot
                {
                    GeneratedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    WindowHours = windowHours
                };

                snapshot.Entries.AddRange(entries.Select(e => new TrendingEntry
                {
                    PostId = e.PostId,
                    Score = e.Score
                }));

                var fingerprint = string.Join(",", entries.Select(e => $"{e.PostId}:{e.Score}"));

                if (fingerprint != lastSent)
                {
                    await responseStream.WriteAsync(snapshot, context.CancellationToken);

                    lastSent = fingerprint;
                }

                await Task.Delay(interval, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _logger.LogInformation("Trending stream closed.");
        }
    }

    public override async Task<ViewSeries> GetViewSeries(GetViewSeriesRequest request, ServerCallContext context)
    {
        RequireOlap();

        var bucket = request.Bucket switch
        {
            SeriesBucket.Minute => CoreBucket.Minute,
            SeriesBucket.Day => CoreBucket.Day,
            _ => CoreBucket.Hour
        };

        var to = request.To?.ToDateTime() ?? DateTime.UtcNow;
        var from = request.From?.ToDateTime() ?? to.AddDays(-1);

        var longest = bucket switch
        {
            CoreBucket.Minute => MaxMinuteSeries,
            CoreBucket.Hour => MaxHourSeries,
            _ => MaxDaySeries
        };

        if (from >= to || to - from > longest)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, $"The range must be positive and at most {longest.TotalDays:0} day(s) for this bucket."));
        }

        var points = await _olap.SeriesAsync(request.PostId, from, to, bucket, context.CancellationToken);

        var response = new ViewSeries();

        response.Points.AddRange(points.Select(p => new SeriesPoint
        {
            BucketStart = Timestamp.FromDateTime(p.BucketStart),
            Views = p.Views,
            UniqueVisitors = p.UniqueVisitors,
            Claps = p.Claps
        }));

        return response;
    }

    public override async Task<TopInWindow> GetTopInWindow(GetTopInWindowRequest request, ServerCallContext context)
    {
        RequireOlap();

        var minutes = Math.Clamp(request.Minutes == 0 ? 60 : request.Minutes, 1, 7 * 24 * 60);
        var limit = Math.Clamp(request.Limit == 0 ? 10 : request.Limit, 1, 50);
        var rankBy = request.Order == WindowOrder.Claps ? CoreRankBy.Claps : CoreRankBy.Views;

        var entries = await _olap.TopInWindowAsync(minutes, rankBy, limit, context.CancellationToken);

        var response = new TopInWindow { Minutes = minutes };

        response.Entries.AddRange(entries.Select(e => new WindowEntry
        {
            PostId = e.PostId,
            Views = e.Views,
            UniqueVisitors = e.UniqueVisitors,
            Claps = e.Claps
        }));

        return response;
    }

    public override async Task<EngagementFunnel> GetEngagementFunnel(
        GetEngagementFunnelRequest request, ServerCallContext context)
    {
        RequireOlap();

        var days = Math.Clamp(request.Days == 0 ? 7 : request.Days, 1, 180);

        var funnel = await _olap.FunnelAsync(request.PostId, days, context.CancellationToken);

        return new EngagementFunnel
        {
            PostId = funnel.PostId,
            Views = funnel.Views,
            UniqueVisitors = funnel.UniqueVisitors,
            Clappers = funnel.Clappers,
            Claps = funnel.Claps,
            Days = days
        };
    }

    private void RequireOlap()
    {
        if (!_olap.IsConfigured)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "ClickHouse is not configured."));
        }
    }

    private static PostStats ToProto(CoreStats stats) => new()
    {
        PostId = stats.PostId,
        Views = stats.Views,
        UniqueVisitors = stats.UniqueVisitors
    };
}
