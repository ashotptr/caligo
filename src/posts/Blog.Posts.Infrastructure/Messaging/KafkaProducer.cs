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

        void Produce(KafkaMessage message);
    }

    public class KafkaProducer : IKafkaProducer, IDisposable
    {
        private readonly Lazy<IProducer<string, byte[]>> _producer;
        private readonly ILogger<KafkaProducer> _logger;

        private bool _disposed;

        public KafkaProducer(IOptions<KafkaOptions> options, ILogger<KafkaProducer> logger)
        {
            _logger = logger;

            _producer = new Lazy<IProducer<string, byte[]>>(() => Build(options.Value));
        }

        private IProducer<string, byte[]> Build(KafkaOptions options)
        {
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,

                MessageSendMaxRetries = 10,
                RetryBackoffMs = 200,

                LingerMs = 5,
                CompressionType = CompressionType.Snappy
            };

            return new ProducerBuilder<string, byte[]>(config)
                .SetErrorHandler((_, error) =>
                {
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

            var activity = BlogActivitySources.Kafka.StartActivity($"{message.Topic} publish", ActivityKind.Producer, parent.ActivityContext);

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

            KafkaTracing.Inject(
                (key, value) => headers.Add(key, Encoding.UTF8.GetBytes(value)),
                activity);

            return new Message<string, byte[]>
            {
                Key = message.Key,
                Value = Encoding.UTF8.GetBytes(message.Payload),
                Headers = headers
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_producer.IsValueCreated)
            {
                try
                {
                    _producer.Value.Flush(TimeSpan.FromSeconds(10));
                }
                catch (ObjectDisposedException)
                {
                }

                _producer.Value.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }
}
