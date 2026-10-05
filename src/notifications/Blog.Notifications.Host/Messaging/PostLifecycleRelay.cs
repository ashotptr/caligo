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

public class PostLifecycleRelay : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly NotificationsOptions _options;
    private readonly ILogger<PostLifecycleRelay> _logger;

    public PostLifecycleRelay(
        IServiceScopeFactory scopes,
        IOptions<NotificationsOptions> options,
        ILogger<PostLifecycleRelay> logger)
    {
        _scopes = scopes;
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

                if (!Handle(result, stoppingToken))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(5));

                    Rewind(consumer, result.TopicPartitionOffset);

                    continue;
                }

                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException ex)
                {
                    _logger.LogWarning(ex, "Committing {Offset} failed; carrying on.", result.TopicPartitionOffset);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private bool Handle(ConsumeResult<string, byte[]> result, CancellationToken cancellationToken)
    {
        var parent = KafkaTracing.Extract(key => Header(result, key));

        using var activity = KafkaTracing.StartConsume(Topics.PostLifecycle, _options.RelayGroup, parent);

        var eventType = Header(result, EventHeaders.EventType);

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

            var fanOut = scope.ServiceProvider.GetRequiredService<NewsletterFanOut>()
                .RunAsync(published.EventId, published.PostId, published.Title, cancellationToken)
                .GetAwaiter().GetResult();

            activity?.SetTag("blog.post_id", published.PostId);
            activity?.SetTag("blog.deliveries_claimed", fanOut.Claimed);
            activity?.SetTag("blog.commands_queued", fanOut.Queued);

            if (fanOut.Claimed == 0)
            {
                _logger.LogInformation(
                    "Event {EventId} for post {PostId}: nothing to send (no subscribers, or already relayed).",
                    published.EventId, published.PostId);
            }
            else
            {
                _logger.LogInformation(
                    "Post {PostId} published: {Claimed} delivery(ies) claimed, {Queued} email command(s) queued.",
                    published.PostId, fanOut.Claimed, fanOut.Queued);
            }

            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            _logger.LogError(ex, "Could not relay event {Offset}.", result.TopicPartitionOffset);

            return false;
        }
    }

    private void Rewind(IConsumer<string, byte[]> consumer, TopicPartitionOffset failed)
    {
        try
        {
            consumer.Seek(failed);
        }
        catch (KafkaException ex)
        {
            _logger.LogWarning(ex, "Could not rewind to {Offset}; its new owner will read it.", failed);
        }
    }

    private static string? Header(ConsumeResult<string, byte[]> result, string name) =>
        result.Message.Headers is not null
        && result.Message.Headers.TryGetLastBytes(name, out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
}
