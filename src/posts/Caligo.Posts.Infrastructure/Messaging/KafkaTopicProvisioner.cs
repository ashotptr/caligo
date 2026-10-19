using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caligo.Contracts;

namespace Caligo.Posts.Infrastructure.Messaging
{
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
                _logger.LogDebug("Kafka topics already exist.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not provision Kafka topics; continuing.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
