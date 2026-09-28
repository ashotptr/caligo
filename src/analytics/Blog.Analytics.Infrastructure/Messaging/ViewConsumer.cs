using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Olap;
using Blog.Analytics.Core.Views;
using Blog.Contracts;
using Blog.Contracts.Events;

namespace Blog.Analytics.Infrastructure.Messaging
{
    public abstract class ViewConsumer : BatchConsumer<PostView>
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ILogger _logger;

        protected ViewConsumer(IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger logger)
            : base(scopes, options, logger)
        {
            _logger = logger;
        }

        protected override string Topic => Topics.PostViewed;

        protected override PostView? Parse(ConsumeResult<string, byte[]> result)
        {
            try
            {
                var viewed = JsonSerializer.Deserialize<PostViewedEvent>(
                    Encoding.UTF8.GetString(result.Message.Value), Json);

                if (viewed is null)
                {
                    return null;
                }

                return new PostView
                {
                    EventId = viewed.EventId.ToString(),
                    PostId = viewed.PostId,
                    ViewedAt = viewed.OccurredAt,
                    Referrer = viewed.Referrer,
                    UserId = viewed.UserId,
                    VisitorHash = viewed.VisitorHash
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "{Group} discarding unreadable message at {Offset}.", GroupId, result.TopicPartitionOffset);

                return null;
            }
        }
    }

    public class RawViewConsumer : ViewConsumer
    {
        public RawViewConsumer(
            IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger<RawViewConsumer> logger)
            : base(scopes, options, logger)
        {
        }

        protected override string GroupId => Options.RawViewsGroup;

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<PostView> batch, CancellationToken cancellationToken)
        {
            await services.GetRequiredService<IViewStore>().RecordManyAsync(batch.Items, cancellationToken);

            return true;
        }
    }

    public class TrendingConsumer : ViewConsumer
    {
        public TrendingConsumer(
            IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options, ILogger<TrendingConsumer> logger)
            : base(scopes, options, logger)
        {
        }

        protected override string GroupId => Options.TrendingGroup;

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<PostView> batch, CancellationToken cancellationToken)
        {
            await services.GetRequiredService<ITrendingStore>().IncrementManyAsync(batch.Items, cancellationToken);

            return true;
        }
    }

    // A third group on the view topic with its own offsets: ClickHouse falling behind, or being down,
    // never slows the Mongo or Redis writers.
    public class OlapViewConsumer : ViewConsumer
    {
        private readonly IOlapSink _sink;

        public OlapViewConsumer(
            IServiceScopeFactory scopes,
            IOptions<AnalyticsOptions> options,
            IOlapSink sink,
            ILogger<OlapViewConsumer> logger)
            : base(scopes, options, logger)
        {
            _sink = sink;
        }

        protected override string GroupId => Options.OlapViewsGroup;

        protected override bool IsEnabled => base.IsEnabled && _sink.IsConfigured;

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<PostView> batch, CancellationToken cancellationToken)
        {
            await _sink.WriteViewsAsync(batch.Items, batch.Id, cancellationToken);

            return true;
        }
    }

    public class OlapClapConsumer : BatchConsumer<ClapEvent>
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IOlapSink _sink;
        private readonly ILogger<OlapClapConsumer> _logger;

        public OlapClapConsumer(
            IServiceScopeFactory scopes,
            IOptions<AnalyticsOptions> options,
            IOlapSink sink,
            ILogger<OlapClapConsumer> logger)
            : base(scopes, options, logger)
        {
            _sink = sink;
            _logger = logger;
        }

        protected override string Topic => Topics.PostClapped;

        protected override string GroupId => Options.OlapClapsGroup;

        protected override bool IsEnabled => base.IsEnabled && _sink.IsConfigured;

        protected override ClapEvent? Parse(ConsumeResult<string, byte[]> result)
        {
            try
            {
                var clapped = JsonSerializer.Deserialize<PostClappedEvent>(
                    Encoding.UTF8.GetString(result.Message.Value), Json);

                if (clapped is null)
                {
                    return null;
                }

                return new ClapEvent
                {
                    EventId = clapped.EventId.ToString(),
                    PostId = clapped.PostId,
                    UserId = clapped.UserId,
                    Delta = clapped.Delta,
                    FirstClap = clapped.FirstClap,
                    ClappedAt = clapped.OccurredAt
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "{Group} discarding unreadable message at {Offset}.", GroupId, result.TopicPartitionOffset);

                return null;
            }
        }

        protected override async Task<bool> HandleAsync(
            IServiceProvider services, ConsumedBatch<ClapEvent> batch, CancellationToken cancellationToken)
        {
            await _sink.WriteClapsAsync(batch.Items, batch.Id, cancellationToken);

            return true;
        }
    }
}
