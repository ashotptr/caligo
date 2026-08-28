using System.Diagnostics;
using System.Text.Json;
using Blog.Contracts;
using Blog.Contracts.Events;
using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Messaging
{
    /// <summary>
    /// Publishes post views straight to Kafka, without going through the outbox.
    ///
    /// That asymmetry is deliberate. A post being published is a state change that other
    /// services must not miss, so it pays for a database round trip and guaranteed delivery. A
    /// view is a measurement: it is high volume, nothing downstream is authoritative on it, and
    /// making every page render write a row to PostgreSQL to protect a counter would cost far
    /// more than an occasional lost view is worth.
    ///
    /// So this send is fire-and-forget and never blocks or throws into the request.
    /// </summary>
    public class KafkaAnalyticsPublisher : IAnalyticsPublisher
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IKafkaProducer _producer;

        public KafkaAnalyticsPublisher(IKafkaProducer producer)
        {
            _producer = producer;
        }

        public void PublishPostViewed(Application.Abstractions.PostViewedEvent viewed)
        {
            var integrationEvent = new Contracts.Events.PostViewedEvent
            {
                PostId = viewed.PostId,
                OccurredAt = viewed.ViewedAt,
                Referrer = viewed.Referrer,
                UserId = viewed.UserId,
                VisitorHash = viewed.VisitorHash
            };

            _producer.Produce(new KafkaMessage(
                Topics.PostViewed,
                integrationEvent.PartitionKey,
                JsonSerializer.Serialize(integrationEvent, Json),
                integrationEvent.EventType,
                integrationEvent.SchemaVersion,
                Activity.Current?.Id));
        }
    }
}
