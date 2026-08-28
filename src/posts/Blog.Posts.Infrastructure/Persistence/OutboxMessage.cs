namespace Blog.Posts.Infrastructure.Persistence
{
    /// <summary>
    /// One integration event waiting to be published, stored in the same database - and in the
    /// same transaction - as the change that produced it.
    ///
    /// This exists because there is no transaction that spans PostgreSQL and Kafka. Publishing
    /// inside the request would either publish an event for a write that later rolls back, or
    /// commit a write whose event was never sent. Writing the event as a row makes the two
    /// atomic; a background dispatcher then relays it, retrying until Kafka accepts it.
    ///
    /// The cost is that delivery becomes at-least-once - a crash between publishing and marking
    /// the row sent replays it - which is why every consumer deduplicates on EventId.
    /// </summary>
    public class OutboxMessage
    {
        /// <summary>Same value as the integration event's EventId, so the chain is traceable end to end.</summary>
        public Guid Id { get; set; }

        public string EventType { get; set; } = string.Empty;

        public string Topic { get; set; } = string.Empty;

        public string PartitionKey { get; set; } = string.Empty;

        /// <summary>The serialised integration event, exactly as it will go on the wire.</summary>
        public string Payload { get; set; } = string.Empty;

        public int SchemaVersion { get; set; } = 1;

        public DateTime OccurredAt { get; set; }

        /// <summary>Null until the broker has acknowledged it. This is what the dispatcher polls on.</summary>
        public DateTime? ProcessedAt { get; set; }

        public int Attempts { get; set; }

        public string? LastError { get; set; }

        /// <summary>
        /// W3C trace context captured at the moment the event was raised, so the eventual
        /// publish and the downstream consumer join the trace that caused them rather than
        /// starting a new one minutes later.
        /// </summary>
        public string? TraceParent { get; set; }
    }
}
