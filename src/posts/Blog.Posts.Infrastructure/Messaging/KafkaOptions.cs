namespace Blog.Posts.Infrastructure.Messaging
{
    public class KafkaOptions
    {
        public const string SectionName = "Kafka";

        public string BootstrapServers { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);

        public string RawViewsGroup { get; set; } = "analytics-raw-writer";

        public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(2);

        public int OutboxBatchSize { get; set; } = 100;

        public int MaxDeliveryAttempts { get; set; } = 10;

        public bool ProvisionTopics { get; set; } = true;
    }
}
