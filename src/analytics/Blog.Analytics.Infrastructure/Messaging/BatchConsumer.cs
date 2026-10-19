using System.Diagnostics;
using System.Text;
using OpenTelemetry.Context.Propagation;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Observability;

namespace Blog.Analytics.Infrastructure.Messaging
{
    public class AnalyticsOptions
    {
        public const string SectionName = "Kafka";

        public string BootstrapServers { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);

        public string RawViewsGroup { get; set; } = "analytics-raw-writer";

        public string TrendingGroup { get; set; } = "analytics-trending";

        public string OlapViewsGroup { get; set; } = "analytics-olap";

        public string OlapClapsGroup { get; set; } = "analytics-olap-claps";

        public string PlaybackGroup { get; set; } = "analytics-playback";

        public int BatchSize { get; set; } = 200;

        public TimeSpan BatchLinger { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed record ConsumedBatch<T>(IReadOnlyList<T> Items, string Id);

    public abstract class BatchConsumer<T> : BackgroundService where T : class
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger _logger;

        private readonly List<T> _batch = new();
        private readonly Dictionary<TopicPartition, (long First, long Last)> _offsets = new();

        private PropagationContext _lastParent;

        protected BatchConsumer(IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger logger)
        {
            _scopes = scopes;
            Options = options.Value;
            _logger = logger;
        }

        protected AnalyticsOptions Options { get; }

        protected abstract string Topic { get; }

        protected abstract string GroupId { get; }

        protected virtual bool IsEnabled => Options.Enabled;

        protected abstract T? Parse(ConsumeResult<string, byte[]> result);

        protected abstract Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<T> batch, CancellationToken cancellationToken);

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!IsEnabled)
            {
                _logger.LogInformation("{Group} is not configured; not started.", GroupId);

                return Task.CompletedTask;
            }

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

                AutoOffsetReset = AutoOffsetReset.Earliest,

                EnableAutoCommit = false,
                SessionTimeoutMs = 45000,
                MaxPollIntervalMs = 300000,

                AllowAutoCreateTopics = false
            };

            using var consumer = new ConsumerBuilder<string, byte[]>(config)
                .SetErrorHandler((_, error) => _logger.LogWarning("Kafka error in {Group}: {Reason}", GroupId, error.Reason))
                .SetPartitionsAssignedHandler((_, partitions) =>
                    _logger.LogInformation("{Group} assigned {Partitions}", GroupId, string.Join(", ", partitions)))
                .SetPartitionsRevokedHandler((_, partitions) => Abandon("revoked", partitions.Count))
                .SetPartitionsLostHandler((_, partitions) => Abandon("lost", partitions.Count))
                .Build();

            consumer.Subscribe(Topic);

            _logger.LogInformation("{Group} consuming {Topic}.", GroupId, Topic);

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
                        _logger.LogInformation("{Group} is waiting for topic {Topic} to be created.", GroupId, Topic);

                        Thread.Sleep(TimeSpan.FromSeconds(5));

                        continue;
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogWarning(ex, "{Group} consume failed; retrying.", GroupId);

                        Thread.Sleep(TimeSpan.FromSeconds(2));

                        continue;
                    }

                    if (result?.Message?.Value is not null)
                    {
                        Track(result);
                    }

                    var full = _batch.Count >= Options.BatchSize;
                    var stale = _offsets.Count > 0 && DateTime.UtcNow - batchStarted > Options.BatchLinger;

                    if (!full && !stale)
                    {
                        continue;
                    }

                    if (StoreUntilDone(consumer, stoppingToken))
                    {
                        Commit(consumer);
                    }

                    _batch.Clear();
                    _offsets.Clear();

                    batchStarted = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                consumer.Close();
            }
        }

        private void Track(ConsumeResult<string, byte[]> result)
        {
            var partition = result.TopicPartition;
            var offset = result.Offset.Value;

            _offsets[partition] = _offsets.TryGetValue(partition, out var range)
                ? (range.First, offset)
                : (offset, offset);

            _lastParent = KafkaTracing.Extract(key =>
                result.Message.Headers is not null
                && result.Message.Headers.TryGetLastBytes(key, out var raw)
                    ? Encoding.UTF8.GetString(raw)
                    : null);

            if (Parse(result) is { } item)
            {
                _batch.Add(item);
            }
        }

        private bool StoreUntilDone(IConsumer<string, byte[]> consumer, CancellationToken stoppingToken)
        {
            var batch = new ConsumedBatch<T>(_batch.ToList(), BatchId());
            var delay = TimeSpan.FromSeconds(1);
            var paused = false;

            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    if (Store(batch, attempt, stoppingToken))
                    {
                        return true;
                    }

                    if (!paused)
                    {
                        consumer.Pause(consumer.Assignment);

                        paused = true;
                    }

                    var retryAt = DateTime.UtcNow + delay;

                    while (DateTime.UtcNow < retryAt)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        ConsumeResult<string, byte[]>? arrived;

                        try
                        {
                            arrived = consumer.Consume(TimeSpan.FromMilliseconds(250));
                        }
                        catch (ConsumeException ex)
                        {
                            _logger.LogWarning(ex, "{Group} poll failed while retrying a batch.", GroupId);

                            continue;
                        }

                        if (arrived?.Message is not null)
                        {
                            consumer.Seek(arrived.TopicPartitionOffset);
                        }

                        if (_offsets.Count == 0)
                        {
                            return false;
                        }
                    }

                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.MaxRetryDelay.Ticks));
                }
            }
            finally
            {
                if (paused)
                {
                    consumer.Resume(consumer.Assignment);
                }
            }
        }

        private bool Store(ConsumedBatch<T> batch, int attempt, CancellationToken cancellationToken)
        {
            if (batch.Items.Count == 0)
            {
                return true;
            }

            using var activity = KafkaTracing.StartConsume(Topic, GroupId, _lastParent);

            activity?.SetTag("messaging.batch.message_count", batch.Items.Count);
            activity?.SetTag("blog.batch.attempt", attempt);

            try
            {
                using var scope = _scopes.CreateScope();

                if (HandleAsync(scope.ServiceProvider, batch, cancellationToken).GetAwaiter().GetResult())
                {
                    return true;
                }

                activity?.SetStatus(ActivityStatusCode.Error, "batch not stored");

                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogWarning(
                    ex, "{Group} could not store {Count} message(s) (attempt {Attempt}); retrying the same batch.",
                    GroupId, batch.Items.Count, attempt);

                return false;
            }
        }

        private void Commit(IConsumer<string, byte[]> consumer)
        {
            try
            {
                consumer.Commit();
            }
            catch (KafkaException ex)
            {
                _logger.LogWarning(ex, "{Group} could not commit its offsets; carrying on.", GroupId);
            }
        }

        private void Abandon(string reason, int partitions)
        {
            if (_offsets.Count > 0)
            {
                _logger.LogInformation(
                    "{Group}: {Count} partition(s) {Reason}; dropping {Messages} uncommitted message(s) for their new owner.",
                    GroupId, partitions, reason, _batch.Count);
            }

            _batch.Clear();
            _offsets.Clear();
        }

        private string BatchId() => string.Join(";", _offsets
            .OrderBy(entry => entry.Key.Topic, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Partition.Value)
            .Select(entry => $"{entry.Key.Topic}:{entry.Key.Partition.Value}:{entry.Value.First}-{entry.Value.Last}"));
    }
}
