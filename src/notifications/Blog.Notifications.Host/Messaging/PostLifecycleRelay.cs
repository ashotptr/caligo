using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Blog.Observability;
using Microsoft.Extensions.Options;
using Blog.Contracts;
using Blog.Contracts.Events;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

/// <summary>
/// Reads the post lifecycle topic and turns one published fact into one command per subscriber.
///
/// This is the fact-to-command boundary, and it is the reason both brokers exist in one system.
/// Kafka carries "a post was published" - said once, to whoever is interested, replayable,
/// with no idea who is listening. RabbitMQ carries "send this email to this address" - one
/// worker, per-message acknowledgement, retried with backoff, parked when it will not succeed.
///
/// Translating between them is exactly what neither broker can do for you, and doing it here
/// keeps the API ignorant of who subscribes and the workers ignorant of why they were asked.
///
/// The asymmetry to notice: replaying this topic is harmless for the analytics projections,
/// which simply recount. Here it would re-send every announcement to every subscriber, so this
/// side deduplicates on (event id, subscriber) before anything reaches the queue.
/// </summary>
public class PostLifecycleRelay : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICommandQueue _queue;
    private readonly NotificationsOptions _options;
    private readonly ILogger<PostLifecycleRelay> _logger;

    public PostLifecycleRelay(
        IServiceScopeFactory scopes,
        ICommandQueue queue,
        IOptions<NotificationsOptions> options,
        ILogger<PostLifecycleRelay> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.KafkaConfigured)
        {
            _logger.LogInformation("No Kafka broker configured; lifecycle relay not started.");

            return Task.CompletedTask;
        }

        return Task.Factory.StartNew(
            () => ConsumeLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.KafkaBootstrapServers,
            GroupId = _options.RelayGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = false,
            SessionTimeoutMs = 45000
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => _logger.LogWarning("Kafka error: {Reason}", error.Reason))
            .Build();

        consumer.Subscribe(Topics.PostLifecycle);

        _logger.LogInformation(
            "Relay consuming {Topic} as group {Group}.", Topics.PostLifecycle, _options.RelayGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;

                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    // The posts service creates this topic; start order is not guaranteed.
                    _logger.LogInformation("Waiting for topic {Topic}.", Topics.PostLifecycle);

                    Thread.Sleep(TimeSpan.FromSeconds(5));

                    continue;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex, "Consume failed; retrying.");

                    Thread.Sleep(TimeSpan.FromSeconds(2));

                    continue;
                }

                if (result?.Message?.Value is null)
                {
                    continue;
                }

                if (Handle(result, stoppingToken))
                {
                    consumer.Commit(result);
                }
                else
                {
                    // Leave the offset so the event is redelivered once whatever failed recovers.
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            consumer.Close();
        }
    }

    private bool Handle(ConsumeResult<string, byte[]> result, CancellationToken cancellationToken)
    {
        // Rejoins the trace of the request that published the post, however long ago that was.
        // Without it, "someone created a post" and "an email went out" are two unrelated
        // traces, and the causal link that makes the fan-out understandable is lost.
        var parent = KafkaTracing.Extract(key => Header(result, key));

        using var activity = KafkaTracing.StartConsume(Topics.PostLifecycle, _options.RelayGroup, parent);

        var eventType = Header(result, EventHeaders.EventType);

        // Only publication is announced. Edits and deletions travel on the same topic and are
        // deliberately ignored rather than mailed about.
        if (eventType is not null && eventType != "post.published")
        {
            return true;
        }

        PostPublishedEvent? published;

        try
        {
            published = JsonSerializer.Deserialize<PostPublishedEvent>(
                Encoding.UTF8.GetString(result.Message.Value), Json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unreadable lifecycle message at {Offset}; skipping.", result.TopicPartitionOffset);

            return true;
        }

        if (published is null || published.EventType != "post.published")
        {
            return true;
        }

        try
        {
            using var scope = _scopes.CreateScope();

            var subscribers = scope.ServiceProvider.GetRequiredService<ISubscriberStore>();
            var ledger = scope.ServiceProvider.GetRequiredService<IDeliveryLedger>();

            var audience = subscribers.ActiveAsync(cancellationToken).GetAwaiter().GetResult();

            if (audience.Count == 0)
            {
                _logger.LogInformation("Post {PostId} published, but nobody is subscribed.", published.PostId);

                return true;
            }

            // Written before anything is enqueued. Rows that already exist come back excluded,
            // so a replayed event produces no commands at all.
            var claimed = ledger.ClaimAsync(
                published.EventId, published.PostId, published.Title, audience, cancellationToken)
                .GetAwaiter().GetResult();

            if (claimed.Count == 0)
            {
                _logger.LogInformation(
                    "Event {EventId} for post {PostId} was already relayed; nothing re-sent.",
                    published.EventId, published.PostId);

                return true;
            }

            var byId = audience.ToDictionary(s => s.Id);
            var queued = new List<Guid>();

            foreach (var delivery in claimed)
            {
                if (!byId.TryGetValue(delivery.SubscriberId, out var subscriber))
                {
                    continue;
                }

                _queue.EnqueueAsync(new SendEmailCommand(
                    delivery.Id,
                    delivery.EventId,
                    subscriber.Id,
                    subscriber.Email,
                    subscriber.DisplayName,
                    delivery.PostId,
                    delivery.PostTitle,
                    subscriber.UnsubscribeToken), cancellationToken).GetAwaiter().GetResult();

                queued.Add(delivery.Id);
            }

            ledger.MarkQueuedAsync(queued, cancellationToken).GetAwaiter().GetResult();

            activity?.SetTag("blog.post_id", published.PostId);
            activity?.SetTag("blog.commands_queued", queued.Count);

            _logger.LogInformation(
                "Post {PostId} published: {Count} email command(s) queued.", published.PostId, queued.Count);

            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            _logger.LogError(ex, "Could not relay event {Offset}.", result.TopicPartitionOffset);

            return false;
        }
    }

    private static string? Header(ConsumeResult<string, byte[]> result, string name) =>
        result.Message.Headers is not null
        && result.Message.Headers.TryGetLastBytes(name, out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
}
