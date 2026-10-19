using System.Diagnostics;
using System.Text.Json;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Media;
using Caligo.Contracts;
using Caligo.Contracts.Events;
using Caligo.Posts.Infrastructure.Messaging;

namespace Caligo.Api.Composition;

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
