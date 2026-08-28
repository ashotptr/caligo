using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Context.Propagation;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Views;
using Blog.Contracts;
using Blog.Observability;
using Blog.Contracts.Events;

namespace Blog.Analytics.Infrastructure.Messaging
{
    public class AnalyticsOptions
    {
        public const string SectionName = "Kafka";

        public string BootstrapServers { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);

        /// <summary>Writes every view to MongoDB, for exact historical aggregation.</summary>
        public string RawViewsGroup { get; set; } = "analytics-raw-writer";

        /// <summary>Maintains the rolling Redis leaderboard.</summary>
        public string TrendingGroup { get; set; } = "analytics-trending";

        public int BatchSize { get; set; } = 200;

        public TimeSpan BatchLinger { get; set; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Base for the two consumer groups that read <c>blog.post.viewed.v1</c>.
    ///
    /// Both read the same topic, independently, at their own offsets. That is the whole reason
    /// views are on a Kafka topic rather than a RabbitMQ queue: with a queue, whichever consumer
    /// received a message would have taken it from the other, so the two projections would each
    /// see roughly half the traffic.
    ///
    /// Offsets are committed only after a batch has been stored. Auto-commit acknowledges at
    /// poll time, which would silently lose whatever was in flight when a process died.
    /// </summary>
    public abstract class ViewConsumer : BackgroundService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger _logger;

        private PropagationContext _lastParent;

        protected ViewConsumer(IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger logger)
        {
            _scopes = scopes;
            Options = options.Value;
            _logger = logger;
        }

        protected AnalyticsOptions Options { get; }

        protected abstract string GroupId { get; }

        /// <summary>Stores a batch. Returning false leaves the offset alone so it is redelivered.</summary>
        protected abstract Task<bool> HandleAsync(
            IServiceProvider services, IReadOnlyList<PostView> batch, CancellationToken cancellationToken);

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Options.Enabled)
            {
                _logger.LogInformation("No Kafka broker configured; {Group} not started.", GroupId);

                return Task.CompletedTask;
            }

            // The Confluent consumer is a blocking, single-threaded API. A dedicated thread keeps
            // it off the thread pool, where a blocking Consume would hold a worker indefinitely.
            return Task.Factory.StartNew(
                () => ConsumeLoop(stoppingToken),
                stoppingToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private void ConsumeLoop(CancellationToken stoppingToken)
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = Options.BootstrapServers,
                GroupId = GroupId,

                // A new group reads the retained history rather than only what arrives next,
                // which is what lets a projection be rebuilt from the log.
                AutoOffsetReset = AutoOffsetReset.Earliest,

                EnableAutoCommit = false,
                SessionTimeoutMs = 45000,
                MaxPollIntervalMs = 300000,

                // Never let a consumer define a topic. Partition count cannot be lowered once
                // set, so an accidental single-partition topic created here would permanently
                // cap the parallelism of every group that reads it.
                AllowAutoCreateTopics = false
            };

            using var consumer = new ConsumerBuilder<string, byte[]>(config)
                .SetErrorHandler((_, error) => _logger.LogWarning("Kafka error in {Group}: {Reason}", GroupId, error.Reason))
                .SetPartitionsAssignedHandler((_, partitions) =>
                    _logger.LogInformation("{Group} assigned {Partitions}", GroupId, string.Join(", ", partitions)))
                .Build();

            consumer.Subscribe(Topics.PostViewed);

            _logger.LogInformation("{Group} consuming {Topic}.", GroupId, Topics.PostViewed);

            var batch = new List<PostView>(Options.BatchSize);
            var batchStarted = DateTime.UtcNow;

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]>? result;

                    try
                    {
                        result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                    }
                    catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                    {
                        // Service start order is not guaranteed, and this consumer can come up
                        // before whatever produces the topic has created it. That is a wait,
                        // not a failure: without this the exception escapes ExecuteAsync and
                        // the default BackgroundServiceExceptionBehavior takes the whole
                        // process down for a condition that resolves itself in seconds.
                        _logger.LogInformation(
                            "{Group} is waiting for topic {Topic} to be created.", GroupId, Topics.PostViewed);

                        Thread.Sleep(TimeSpan.FromSeconds(5));

                        continue;
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogWarning(ex, "{Group} consume failed; retrying.", GroupId);

                        Thread.Sleep(TimeSpan.FromSeconds(2));

                        continue;
                    }

                    if (result?.Message?.Value is not null && TryRead(result) is { } view)
                    {
                        batch.Add(view);
                    }

                    var full = batch.Count >= Options.BatchSize;
                    var stale = batch.Count > 0 && DateTime.UtcNow - batchStarted > Options.BatchLinger;

                    if (!full && !stale)
                    {
                        continue;
                    }

                    // One span per batch rather than per message: the batch is the unit of work
                    // that is actually stored and committed, so it is the unit worth timing.
                    using (var activity = StartBatchSpan(batch.Count))
                    {
                        if (Store(batch, stoppingToken))
                        {
                            consumer.Commit();
                        }
                        else
                        {
                            activity?.SetStatus(ActivityStatusCode.Error, "batch not stored");

                            // Offsets stay put, so these records come back round.
                            Thread.Sleep(TimeSpan.FromSeconds(5));
                        }
                    }
                    batch.Clear();
                    batchStarted = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            finally
            {
                // Leave the group cleanly so partitions are reassigned immediately rather than
                // after the session times out.
                consumer.Close();
            }
        }

        private bool Store(List<PostView> batch, CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
            {
                return true;
            }

            try
            {
                using var scope = _scopes.CreateScope();

                return HandleAsync(scope.ServiceProvider, batch, cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Group} could not store {Count} view(s); redelivering.", GroupId, batch.Count);

                return false;
            }
        }

        private Activity? StartBatchSpan(int size)
        {
            var activity = KafkaTracing.StartConsume(Topics.PostViewed, GroupId, _lastParent);

            activity?.SetTag("messaging.batch.message_count", size);

            return activity;
        }

        private PostView? TryRead(ConsumeResult<string, byte[]> result)
        {
            // Remembers the trace the most recent message came from, so the batch span attaches
            // to a real producer rather than starting an orphan trace. A batch can span several
            // traces; this links it to the latest, which is the pragmatic choice short of
            // adding a span link per message.
            _lastParent = KafkaTracing.Extract(key =>
                result.Message.Headers is not null
                && result.Message.Headers.TryGetLastBytes(key, out var raw)
                    ? Encoding.UTF8.GetString(raw)
                    : null);

            try
            {
                var viewed = JsonSerializer.Deserialize<PostViewedEvent>(
                    Encoding.UTF8.GetString(result.Message.Value), Json);

                if (viewed is null)
                {
                    return null;
                }

                return new PostView
                {
                    EventId = viewed.EventId.ToString(),
                    PostId = viewed.PostId,
                    ViewedAt = viewed.OccurredAt,
                    Referrer = viewed.Referrer,
                    UserId = viewed.UserId,
                    VisitorHash = viewed.VisitorHash
                };
            }
            catch (JsonException ex)
            {
                // Unreadable on the wire. Stopping the partition for one bad record would block
                // every later view behind it, so it is dropped and reported.
                _logger.LogError(ex, "{Group} discarding unreadable message at {Offset}.", GroupId, result.TopicPartitionOffset);

                return null;
            }
        }
    }

    /// <summary>Writes every view to MongoDB. Deduplicates on event id via a unique index.</summary>
    public class RawViewConsumer : ViewConsumer
    {
        public RawViewConsumer(
            IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger<RawViewConsumer> logger)
            : base(scopes, options, logger)
        {
        }

        protected override string GroupId => Options.RawViewsGroup;

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, IReadOnlyList<PostView> batch, CancellationToken cancellationToken)
        {
            await services.GetRequiredService<IViewStore>().RecordManyAsync(batch, cancellationToken);

            return true;
        }
    }

    /// <summary>
    /// Maintains the Redis leaderboard from the same stream, at its own offset.
    ///
    /// It does not deduplicate. A replayed batch inflates a disposable ranking that ages out
    /// within hours, and paying for exactness here would cost a round trip per view for no
    /// benefit the reader could notice.
    /// </summary>
    public class TrendingConsumer : ViewConsumer
    {
        public TrendingConsumer(
            IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger<TrendingConsumer> logger)
            : base(scopes, options, logger)
        {
        }

        protected override string GroupId => Options.TrendingGroup;

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, IReadOnlyList<PostView> batch, CancellationToken cancellationToken)
        {
            await services.GetRequiredService<ITrendingStore>().IncrementManyAsync(batch, cancellationToken);

            return true;
        }
    }
}
