using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Playback;
using Blog.Contracts;
using Blog.Contracts.Events;

namespace Blog.Analytics.Infrastructure.Messaging
{
    public class PlaybackConsumer : BatchConsumer<PlaybackEventRow>
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IPlaybackStore _store;
        private readonly ILogger<PlaybackConsumer> _logger;

        public PlaybackConsumer(
            IServiceScopeFactory scopes,
            IOptions<AnalyticsOptions> options,
            IPlaybackStore store,
            ILogger<PlaybackConsumer> logger)
            : base(scopes, options, logger)
        {
            _store = store;
            _logger = logger;
        }

        protected override string Topic => Topics.PlaybackEvents;

        protected override string GroupId => Options.PlaybackGroup;

        protected override bool IsEnabled => base.IsEnabled && _store.IsConfigured;

        protected override PlaybackEventRow? Parse(ConsumeResult<string, byte[]> result)
        {
            try
            {
                var played = JsonSerializer.Deserialize<PlaybackEvent>(Encoding.UTF8.GetString(result.Message.Value), Json);

                if (played is null)
                {
                    return null;
                }

                return new PlaybackEventRow
                {
                    EventId = played.EventId.ToString(),
                    DeviceId = played.DeviceId,
                    Seq = played.Seq,
                    SessionId = played.SessionId.ToString(),
                    ContentId = played.ContentId,
                    Kind = played.Kind,
                    Delivery = played.Delivery,
                    OccurredAt = played.OccurredAt,
                    Position = played.Position,
                    DurationMs = played.DurationMs,
                    FromHeight = played.FromHeight,
                    ToHeight = played.ToHeight,
                    Detail = played.Detail
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "{Group} discarding unreadable message at {Offset}.", GroupId, result.TopicPartitionOffset);

                return null;
            }
        }

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<PlaybackEventRow> batch, CancellationToken cancellationToken)
        {
            await _store.WriteAsync(batch.Items, batch.Id, cancellationToken);

            return true;
        }
    }
}
