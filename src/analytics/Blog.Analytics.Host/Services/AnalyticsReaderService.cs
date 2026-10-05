using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Blog.Contracts.Analytics;

using IViewStore = Blog.Analytics.Core.Views.IViewStore;
using ITrendingStore = Blog.Analytics.Core.Views.ITrendingStore;
using CoreStats = Blog.Analytics.Core.Views.PostStats;
using IOlapQueries = Blog.Analytics.Core.Olap.IOlapQueries;
using CoreBucket = Blog.Analytics.Core.Olap.SeriesBucket;
using CoreRankBy = Blog.Analytics.Core.Olap.RankBy;

namespace Blog.Analytics.Host.Services;

public class AnalyticsReaderService : AnalyticsReader.AnalyticsReaderBase
{
    private const int MaxBatchSize = 100;

    private static readonly TimeSpan MaxMinuteSeries = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxHourSeries = TimeSpan.FromDays(31);
    private static readonly TimeSpan MaxDaySeries = TimeSpan.FromDays(366);

    private readonly IViewStore _views;
    private readonly ITrendingStore _trending;
    private readonly IOlapQueries _olap;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalyticsReaderService> _logger;

    public AnalyticsReaderService(
        IViewStore views,
        ITrendingStore trending,
        IOlapQueries olap,
        IConfiguration configuration,
        ILogger<AnalyticsReaderService> logger)
    {
        _views = views;
        _trending = trending;
        _olap = olap;
        _configuration = configuration;
        _logger = logger;
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
