using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Blog.Contracts.Analytics;

// The generated messages are imported plainly, because this file mostly speaks the wire
// contract. The internal model deliberately shares two of those names - PostStats and
// TrendingEntry describe the same idea on either side of the boundary - so it comes in by
// alias rather than by a second wildcard import that would make every use ambiguous.
using IViewStore = Blog.Analytics.Core.Views.IViewStore;
using ITrendingStore = Blog.Analytics.Core.Views.ITrendingStore;
using CoreStats = Blog.Analytics.Core.Views.PostStats;

namespace Blog.Analytics.Host.Services;

/// <summary>
/// The gRPC read API.
///
/// Everything here returns counts keyed by post id and nothing else. This service has no idea
/// what a post title is, and asking it would mean either duplicating the posts database or
/// calling back into the API that called it. The caller joins titles from its own database,
/// which is what keeps the boundary somewhere defensible.
/// </summary>
public class AnalyticsReaderService : AnalyticsReader.AnalyticsReaderBase
{
    private const int MaxBatchSize = 100;

    private readonly IViewStore _views;
    private readonly ITrendingStore _trending;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalyticsReaderService> _logger;

    public AnalyticsReaderService(
        IViewStore views,
        ITrendingStore trending,
        IConfiguration configuration,
        ILogger<AnalyticsReaderService> logger)
    {
        _views = views;
        _trending = trending;
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
            // Bounded so one caller cannot ask for an aggregation over every post there is.
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

    /// <summary>
    /// Pushes a trending snapshot on an interval for as long as the caller listens.
    ///
    /// Server streaming is the reason this API is gRPC rather than REST. The alternative is
    /// every dashboard polling on a timer, which costs a request per client per interval
    /// whether or not anything changed, and still shows stale data between polls.
    /// </summary>
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
            // Suppresses the push when nothing has moved, so an idle dashboard costs one
            // read against Redis per interval and no traffic at all on the wire.
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
            // The client hung up, which is the normal way a stream ends.
        }
        finally
        {
            _logger.LogInformation("Trending stream closed.");
        }
    }

    private static PostStats ToProto(CoreStats stats) => new()
    {
        PostId = stats.PostId,
        Views = stats.Views,
        UniqueVisitors = stats.UniqueVisitors
    };
}
