namespace Caligo.Posts.Infrastructure.Persistence
{
    public class OutboxMessage
    {
        public Guid Id { get; set; }

        public string EventType { get; set; } = string.Empty;

        public string Topic { get; set; } = string.Empty;

        public string PartitionKey { get; set; } = string.Empty;

        public string Payload { get; set; } = string.Empty;

        public int SchemaVersion { get; set; } = 1;

        public DateTime OccurredAt { get; set; }

        public DateTime? ProcessedAt { get; set; }

        public int Attempts { get; set; }

        public string? LastError { get; set; }

        public string? TraceParent { get; set; }
    }
}
