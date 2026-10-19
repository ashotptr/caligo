using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Caligo.Contracts.Analytics;
using Caligo.Posts.Application.Abstractions;

namespace Caligo.Posts.Infrastructure.Messaging
{
    public class AnalyticsClientOptions
    {
        public const string SectionName = "Analytics";

        public string Address { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(Address);

        public TimeSpan Deadline { get; set; } = TimeSpan.FromMilliseconds(150);

        public TimeSpan OlapDeadline { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan LabDeadline { get; set; } = TimeSpan.FromSeconds(10);
    }

    public class GrpcViewStatsClient : IViewStatsClient
    {
        private readonly AnalyticsReader.AnalyticsReaderClient _client;
        private readonly AnalyticsClientOptions _options;
        private readonly ResiliencePipeline _pipeline;
        private readonly ILogger<GrpcViewStatsClient> _logger;

        public GrpcViewStatsClient(
            AnalyticsReader.AnalyticsReaderClient client,
            IOptions<AnalyticsClientOptions> options,
            ILogger<GrpcViewStatsClient> logger)
        {
            _client = client;
            _options = options.Value;
            _logger = logger;

            _pipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<RpcException>(IsTransient),
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromMilliseconds(20),
                    BackoffType = DelayBackoffType.Constant
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<RpcException>(IsTransient),
                    FailureRatio = 0.5,
                    MinimumThroughput = 8,
                    SamplingDuration = TimeSpan.FromSeconds(15),
                    BreakDuration = TimeSpan.FromSeconds(10),
                    OnOpened = args =>
                    {
                        _logger.LogWarning(
                            "Analytics circuit opened for {Duration}; view counts are unavailable.",
                            args.BreakDuration);

                        return ValueTask.CompletedTask;
                    },
                    OnClosed = _ =>
                    {
                        _logger.LogInformation("Analytics circuit closed; view counts are back.");

                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        public bool IsConfigured => _options.Enabled;

        private CallOptions Call(CancellationToken cancellationToken) =>
            new(deadline: DateTime.UtcNow.Add(_options.Deadline), cancellationToken: cancellationToken);

        private CallOptions OlapCall(CancellationToken cancellationToken) =>
            new(deadline: DateTime.UtcNow.Add(_options.OlapDeadline), cancellationToken: cancellationToken);

        public Task<ViewStats?> GetForPostAsync(int postId, CancellationToken cancellationToken = default) =>
            TryAsync(
                async token =>
                {
                    var stats = await _client.GetPostStatsAsync(
                        new GetPostStatsRequest { PostId = postId }, Call(token));

                    return (ViewStats?)new ViewStats(stats.PostId, stats.Views, stats.UniqueVisitors);
                },
                nameof(GetForPostAsync),
                cancellationToken);

        public Task<IReadOnlyDictionary<int, ViewStats>?> GetForPostsAsync(
            IReadOnlyList<int> postIds, CancellationToken cancellationToken = default)
        {
            if (postIds.Count == 0)
            {
                return Task.FromResult<IReadOnlyDictionary<int, ViewStats>?>(
                    new Dictionary<int, ViewStats>());
            }

            return TryAsync(
                async token =>
                {
                    var request = new GetPostStatsBatchRequest();
                    request.PostIds.AddRange(postIds);

                    var batch = await _client.GetPostStatsBatchAsync(request, Call(token));

                    return (IReadOnlyDictionary<int, ViewStats>?)batch.Stats.ToDictionary(
                        s => s.PostId,
                        s => new ViewStats(s.PostId, s.Views, s.UniqueVisitors));
                },
                nameof(GetForPostsAsync),
                cancellationToken);
        }

        public Task<IReadOnlyList<ViewStats>?> GetTopPostsAsync(
            int days, int limit, CancellationToken cancellationToken = default) =>
            TryAsync(
                async token =>
                {
                    var top = await _client.GetTopPostsAsync(
                        new GetTopPostsRequest { Days = days, Limit = limit }, Call(token));

                    return (IReadOnlyList<ViewStats>?)top.Posts
                        .Select(s => new ViewStats(s.PostId, s.Views, s.UniqueVisitors))
                        .ToList();
                },
                nameof(GetTopPostsAsync),
                cancellationToken);

        public Task<IReadOnlyList<Application.Abstractions.SeriesPoint>?> GetSeriesAsync(
            int postId, DateTime from, DateTime to, SeriesGranularity granularity,
            CancellationToken cancellationToken = default) =>
            TryAsync(
                async token =>
                {
                    var series = await _client.GetViewSeriesAsync(
                        new GetViewSeriesRequest
                        {
                            PostId = postId,
                            From = Timestamp.FromDateTime(DateTime.SpecifyKind(from, DateTimeKind.Utc)),
                            To = Timestamp.FromDateTime(DateTime.SpecifyKind(to, DateTimeKind.Utc)),
                            Bucket = granularity switch
                            {
                                SeriesGranularity.Minute => SeriesBucket.Minute,
                                SeriesGranularity.Day => SeriesBucket.Day,
                                _ => SeriesBucket.Hour
                            }
                        },
                        OlapCall(token));

                    return (IReadOnlyList<Application.Abstractions.SeriesPoint>?)series.Points
                        .Select(p => new Application.Abstractions.SeriesPoint(
                            p.BucketStart.ToDateTime(), p.Views, p.UniqueVisitors, p.Claps))
                        .ToList();
                },
                nameof(GetSeriesAsync),
                cancellationToken);

        public Task<IReadOnlyList<WindowPost>?> GetTopInWindowAsync(
            int minutes, WindowRanking ranking, int limit, CancellationToken cancellationToken = default) =>
            TryAsync(
                async token =>
                {
                    var top = await _client.GetTopInWindowAsync(
                        new GetTopInWindowRequest
                        {
                            Minutes = minutes,
                            Limit = limit,
                            Order = ranking == WindowRanking.Claps ? WindowOrder.Claps : WindowOrder.Views
                        },
                        OlapCall(token));

                    return (IReadOnlyList<WindowPost>?)top.Entries
                        .Select(e => new WindowPost(e.PostId, e.Views, e.UniqueVisitors, e.Claps))
                        .ToList();
                },
                nameof(GetTopInWindowAsync),
                cancellationToken);

        public Task<Engagement?> GetEngagementAsync(int postId, int days, CancellationToken cancellationToken = default) =>
            TryAsync(
                async token =>
                {
                    var funnel = await _client.GetEngagementFunnelAsync(
                        new GetEngagementFunnelRequest { PostId = postId, Days = days }, OlapCall(token));

                    return (Engagement?)new Engagement(
                        funnel.PostId, funnel.Views, funnel.UniqueVisitors, funnel.Clappers, funnel.Claps, funnel.Days);
                },
                nameof(GetEngagementAsync),
                cancellationToken);

        public async IAsyncEnumerable<IReadOnlyList<TrendingPost>> StreamTrendingAsync(
            int windowHours, int limit, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                yield break;
            }

            using var call = _client.StreamTrending(
                new StreamTrendingRequest { WindowHours = windowHours, Limit = limit },
                cancellationToken: cancellationToken);

            while (true)
            {
                TrendingSnapshot? snapshot;

                try
                {
                    if (!await call.ResponseStream.MoveNext(cancellationToken))
                    {
                        break;
                    }

                    snapshot = call.ResponseStream.Current;
                }
                catch (RpcException ex)
                {
                    _logger.LogWarning("Trending stream ended: {Status}", ex.StatusCode);

                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                yield return snapshot.Entries
                    .Select(e => new TrendingPost(e.PostId, e.Score))
                    .ToList();
            }
        }

        private async Task<T?> TryAsync<T>(
            Func<CancellationToken, Task<T?>> call, string operation, CancellationToken cancellationToken)
            where T : class
        {
            if (!_options.Enabled)
            {
                return null;
            }

            try
            {
                return await _pipeline.ExecuteAsync(
                    async token => await call(token), cancellationToken);
            }
            catch (BrokenCircuitException)
            {
                return null;
            }
            catch (RpcException ex)
            {
                _logger.LogWarning(
                    "Analytics {Operation} failed ({Status}); continuing without view counts.",
                    operation, ex.StatusCode);

                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Analytics {Operation} exceeded its deadline.", operation);

                return null;
            }
        }

        private static bool IsTransient(RpcException exception) => exception.StatusCode is
            StatusCode.Unavailable or
            StatusCode.DeadlineExceeded or
            StatusCode.ResourceExhausted or
            StatusCode.Aborted or
            StatusCode.Internal;
    }
}
