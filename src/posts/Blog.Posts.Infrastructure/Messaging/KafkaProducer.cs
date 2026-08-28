using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Blog.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Contracts;

namespace Blog.Posts.Infrastructure.Messaging
{
    public record KafkaMessage(
        string Topic,
        string Key,
        string Payload,
        string EventType,
        int SchemaVersion,
        string? TraceParent);

    public interface IKafkaProducer
    {
        Task ProduceAsync(KafkaMessage message, CancellationToken cancellationToken = default);

        /// <summary>Fire-and-forget. Used for streams where a lost message is acceptable.</summary>
        void Produce(KafkaMessage message);
    }

    /// <summary>
    /// One producer for the process. Confluent's producer is thread-safe and maintains its own
    /// connection pool and batching; creating one per call would defeat both and leak sockets.
    /// </summary>
    public class KafkaProducer : IKafkaProducer, IDisposable
    {
        private readonly Lazy<IProducer<string, byte[]>> _producer;
        private readonly ILogger<KafkaProducer> _logger;

        private bool _disposed;

        public KafkaProducer(IOptions<KafkaOptions> options, ILogger<KafkaProducer> logger)
        {
            _logger = logger;

            // Built on first use. The producer is a singleton and is therefore constructed even
            // in configurations where Kafka is switched off, and creating a client against an
            // empty broker list would throw during startup.
            _producer = new Lazy<IProducer<string, byte[]>>(() => Build(options.Value));
        }

        private IProducer<string, byte[]> Build(KafkaOptions options)
        {
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,

                // Wait for all in-sync replicas. Anything less means an acknowledged write can
                // still be lost to a broker failover, which would defeat the outbox upstream.
                Acks = Acks.All,

                // Makes the broker discard duplicates caused by the producer's own retries, so a
                // retry cannot turn one logical event into two records on the partition.
                EnableIdempotence = true,

                MessageSendMaxRetries = 10,
                RetryBackoffMs = 200,

                // A few milliseconds of batching: worth far more in throughput than it costs in
                // latency, since nothing here is waiting on the ack synchronously.
                LingerMs = 5,
                CompressionType = CompressionType.Snappy
            };

            return new ProducerBuilder<string, byte[]>(config)
                .SetErrorHandler((_, error) =>
                {
                    // Transient broker errors are recovered by the client itself; only genuinely
                    // fatal ones need attention.
                    if (error.IsFatal)
                    {
                        _logger.LogError("Fatal Kafka producer error: {Reason}", error.Reason);
                    }
                    else
                    {
                        _logger.LogWarning("Kafka producer error: {Reason}", error.Reason);
                    }
                })
                .Build();
        }

        public async Task ProduceAsync(KafkaMessage message, CancellationToken cancellationToken = default)
        {
            // The message may carry a traceparent captured when it was written to the outbox,
            // minutes ago. Restoring it makes this publish a child of the request that caused
            // it rather than an orphan.
            using var activity = StartSpan(message);

            var result = await _producer.Value.ProduceAsync(
                message.Topic, BuildMessage(message, activity), cancellationToken);

            activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            activity?.SetTag("messaging.kafka.offset", result.Offset.Value);

            _logger.LogDebug(
                "Published {EventType} to {TopicPartitionOffset}.", message.EventType, result.TopicPartitionOffset);
        }

        private Activity? StartSpan(KafkaMessage message)
        {
            if (message.TraceParent is null)
            {
                return KafkaTracing.StartProduce(message.Topic, message.Key);
            }

            var parent = KafkaTracing.FromTraceParent(message.TraceParent);

            var activity = BlogActivitySources.Kafka.StartActivity(
                $"{message.Topic} publish", ActivityKind.Producer, parent.ActivityContext);

            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.operation", "publish");
            activity?.SetTag("messaging.destination.name", message.Topic);
            activity?.SetTag("messaging.kafka.message.key", message.Key);

            return activity;
        }

        public void Produce(KafkaMessage message)
        {
            try
            {
                // Hands the record to the client's background send thread and returns. The
                // callback only reports failures; nothing upstream is waiting on the outcome.
                using var activity = KafkaTracing.StartProduce(message.Topic, message.Key);

                _producer.Value.Produce(message.Topic, BuildMessage(message, activity), report =>
                {
                    if (report.Error.IsError)
                    {
                        _logger.LogWarning(
                            "Dropped {EventType}: {Reason}", message.EventType, report.Error.Reason);
                    }
                });
            }
            catch (ProduceException<string, byte[]> ex)
            {
                // Thrown when the local queue is full, i.e. the broker is unreachable and the
                // buffer has filled. Dropping is the correct answer for a lossy stream.
                _logger.LogWarning(ex, "Kafka send queue is full; dropping {EventType}.", message.EventType);
            }
        }

        private static Message<string, byte[]> BuildMessage(KafkaMessage message, Activity? activity)
        {
            var headers = new Headers
            {
                { EventHeaders.EventType, Encoding.UTF8.GetBytes(message.EventType) },
                { EventHeaders.SchemaVersion, Encoding.UTF8.GetBytes(message.SchemaVersion.ToString()) }
            };

            // Writes traceparent (and tracestate, if any) so the consumer can rejoin this trace.
            // HTTP gets this for free from its client libraries; across a broker nothing does it
            // unless the producer does.
            KafkaTracing.Inject(
                (key, value) => headers.Add(key, Encoding.UTF8.GetBytes(value)),
                activity);

            return new Message<string, byte[]>
            {
                // The key decides the partition, which is what keeps one post's events ordered.
                Key = message.Key,
                Value = Encoding.UTF8.GetBytes(message.Payload),
                Headers = headers
            };
        }

        public void Dispose()
        {
            // Dispose is required to be safe to call more than once.
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_producer.IsValueCreated)
            {
                try
                {
                    // Blocks until buffered records are delivered, so a graceful shutdown does
                    // not throw away messages already accepted from the outbox.
                    _producer.Value.Flush(TimeSpan.FromSeconds(10));
                }
                catch (ObjectDisposedException)
                {
                    // The native handle has already gone; nothing left to flush.
                }

                _producer.Value.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }
}
