using System.Diagnostics;
using System.Text.Json;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Media;
using Blog.Contracts;
using Blog.Contracts.Events;
using Blog.Posts.Infrastructure.Messaging;

namespace Blog.Api.Composition;

public class KafkaPlaybackEvents : IPlaybackEventSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IKafkaProducer _producer;

    public KafkaPlaybackEvents(IKafkaProducer producer)
    {
        _producer = producer;
    }

    public void Publish(IReadOnlyList<PlaybackEventRecord> events)
    {
        foreach (var record in events)
        {
            var integrationEvent = new PlaybackEvent
            {
                EventId = record.EventId,
                OccurredAt = DateTime.SpecifyKind(record.OccurredAt, DateTimeKind.Utc),
                SessionId = record.SessionId,
                DeviceId = record.DeviceId,
                Seq = record.Seq,
                ContentId = record.ContentId,
                Kind = record.Type,
                Delivery = record.Delivery,
                Position = record.Position,
                DurationMs = record.DurationMs,
                FromHeight = record.FromHeight,
                ToHeight = record.ToHeight,
                Detail = record.Detail
            };

            _producer.Produce(new KafkaMessage(
                Topics.PlaybackEvents,
                integrationEvent.PartitionKey,
                JsonSerializer.Serialize(integrationEvent, Json),
                integrationEvent.EventType,
                integrationEvent.SchemaVersion,
                Activity.Current?.Id));
        }
    }
}

public class NullPlaybackEvents : IPlaybackEventSink
{
    public void Publish(IReadOnlyList<PlaybackEventRecord> events)
    {
    }
}
