using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Contracts;

namespace Blog.Posts.Infrastructure.Messaging
{
    /// <summary>
    /// Creates the topics this service depends on, with the partition counts and retention they
    /// are supposed to have.
    ///
    /// Kafka will happily auto-create a topic on first use, which is exactly the problem: it
    /// would appear with one partition and the broker's default retention, silently capping
    /// consumer parallelism at one and making replay impossible. Partition count cannot be
    /// reduced later, so getting it wrong at creation is not a mistake you can simply undo.
    /// </summary>
    public class KafkaTopicProvisioner : IHostedService
    {
        private readonly KafkaOptions _options;
        private readonly ILogger<KafkaTopicProvisioner> _logger;

        public KafkaTopicProvisioner(IOptions<KafkaOptions> options, ILogger<KafkaTopicProvisioner> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.Enabled || !_options.ProvisionTopics)
            {
                return;
            }

            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();

            var specs = Topics.Partitions.Select(topic => new TopicSpecification
            {
                Name = topic.Key,
                NumPartitions = topic.Value,

                // Single-broker development cluster. A real deployment would raise this and set
                // min.insync.replicas to match the producer's Acks.All.
                ReplicationFactor = 1,
                Configs = new Dictionary<string, string>
                {
                    ["retention.ms"] = ((long)Topics.Retention[topic.Key].TotalMilliseconds).ToString()
                }
            }).ToList();

            try
            {
                await admin.CreateTopicsAsync(specs);

                _logger.LogInformation(
                    "Created Kafka topics: {Topics}", string.Join(", ", specs.Select(s => s.Name)));
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r =>
                r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
            {
                // Every service in the stack tries this at startup, so losing the race is the
                // normal outcome rather than a problem.
                _logger.LogDebug("Kafka topics already exist.");
            }
            catch (Exception ex)
            {
                // Startup continues: the relay retries indefinitely, so a broker that is not up
                // yet delays delivery rather than preventing the API from serving requests.
                _logger.LogWarning(ex, "Could not provision Kafka topics; continuing.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
