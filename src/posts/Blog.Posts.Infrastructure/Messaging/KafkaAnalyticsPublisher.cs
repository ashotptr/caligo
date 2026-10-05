using System.Diagnostics;
using System.Text.Json;
using Blog.Contracts;
using Blog.Contracts.Events;
using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Messaging
{
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

        public void PublishPostClapped(Application.Abstractions.PostClappedEvent clapped)
        {
            var integrationEvent = new Contracts.Events.PostClappedEvent
            {
                PostId = clapped.PostId,
                OccurredAt = clapped.ClappedAt,
                UserId = clapped.UserId,
                Delta = clapped.Delta,
                ReaderTotal = clapped.ReaderTotal,
                FirstClap = clapped.FirstClap
            };

            _producer.Produce(new KafkaMessage(
                Topics.PostClapped,
                integrationEvent.PartitionKey,
                JsonSerializer.Serialize(integrationEvent, Json),
                integrationEvent.EventType,
                integrationEvent.SchemaVersion,
                Activity.Current?.Id));
        }
    }
}
