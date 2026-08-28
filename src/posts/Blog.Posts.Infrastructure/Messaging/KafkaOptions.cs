namespace Blog.Posts.Infrastructure.Messaging
{
    public class KafkaOptions
    {
        public const string SectionName = "Kafka";

        /// <summary>Comma-separated broker list. Empty disables Kafka entirely.</summary>
        public string BootstrapServers { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);

        /// <summary>Consumer group for the writer that records raw views in MongoDB.</summary>
        public string RawViewsGroup { get; set; } = "analytics-raw-writer";

        /// <summary>How long the dispatcher waits between polls when the outbox is empty.</summary>
        public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Rows claimed per dispatcher pass.</summary>
        public int OutboxBatchSize { get; set; } = 100;

        /// <summary>
        /// After this many failures a message stops being retried and is left in the table for
        /// inspection. Without a ceiling one poison message blocks everything behind it forever.
        /// </summary>
        public int MaxDeliveryAttempts { get; set; } = 10;

        /// <summary>
        /// Create topics at startup rather than relying on Kafka's auto-creation, which would
        /// silently give every topic one partition and the broker's default retention.
        /// </summary>
        public bool ProvisionTopics { get; set; } = true;
    }
}
