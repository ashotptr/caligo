using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

public class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>amqp://user:pass@host:5672/. Empty disables outbound email entirely.</summary>
    public string RabbitMq { get; set; } = string.Empty;

    public bool RabbitConfigured => !string.IsNullOrWhiteSpace(RabbitMq);

    public string KafkaBootstrapServers { get; set; } = string.Empty;

    public bool KafkaConfigured => !string.IsNullOrWhiteSpace(KafkaBootstrapServers);

    /// <summary>Consumer group reading the post lifecycle topic.</summary>
    public string RelayGroup { get; set; } = "notifications-relay";

    /// <summary>How many emails one worker will have in flight at a time.</summary>
    public ushort Prefetch { get; set; } = 10;

    /// <summary>Base URL used to build unsubscribe links.</summary>
    public string SiteUrl { get; set; } = "http://localhost:8080";

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a Pending row waits before the sweeper assumes it was stranded.</summary>
    public TimeSpan StrandedAfter { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Publishes email commands, and moves failed ones along the retry ladder.
/// </summary>
public class RabbitCommandQueue : ICommandQueue, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NotificationsOptions _options;
    private readonly ILogger<RabbitCommandQueue> _logger;
    private readonly object _gate = new();

    private IConnection? _connection;
    private IModel? _channel;
    private bool _disposed;

    public RabbitCommandQueue(IOptions<NotificationsOptions> options, ILogger<RabbitCommandQueue> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.RabbitConfigured;

    public Task EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
    {
        Publish(RabbitTopology.Exchange, RabbitTopology.EmailRoutingKey, command);

        return Task.CompletedTask;
    }

    public Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default)
    {
        var nextAttempt = command.Attempt + 1;

        if (nextAttempt >= RabbitTopology.MaxAttempts)
        {
            // Out of attempts. Parking keeps the message and its history where someone can see
            // it, rather than discarding a real email nobody ever received.
            _logger.LogError(
                "Delivery {DeliveryId} to {Email} failed {Attempts} times and is being parked: {Error}",
                command.DeliveryId, command.Email, nextAttempt, error);

            Publish(RabbitTopology.DeadLetterExchange, string.Empty, command with { Attempt = nextAttempt }, error);

            return Task.CompletedTask;
        }

        var delay = RabbitTopology.RetryDelays[command.Attempt];

        _logger.LogWarning(
            "Delivery {DeliveryId} to {Email} failed (attempt {Attempt}); retrying in {Delay}: {Error}",
            command.DeliveryId, command.Email, nextAttempt, delay, error);

        // Into the TTL queue for this attempt. It waits there and dead-letters itself back to
        // the working queue when the TTL expires - no timer, no scheduler, no plugin.
        Publish(
            RabbitTopology.RetryExchange,
            RabbitTopology.RetryRoutingKey(command.Attempt),
            command with { Attempt = nextAttempt },
            error);

        return Task.CompletedTask;
    }

    private void Publish(string exchange, string routingKey, SendEmailCommand command, string? error = null)
    {
        if (!IsConfigured)
        {
            return;
        }

        var channel = EnsureChannel();

        if (channel is null)
        {
            return;
        }

        var properties = channel.CreateBasicProperties();

        // Survives a broker restart. An unroutable in-memory message would be an email silently
        // never sent.
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.MessageId = command.DeliveryId.ToString();

        properties.Headers = new Dictionary<string, object>
        {
            ["x-attempt"] = command.Attempt
        };

        if (error is not null)
        {
            properties.Headers["x-last-error"] = Encoding.UTF8.GetBytes(
                error.Length > 500 ? error[..500] : error);
        }

        lock (_gate)
        {
            channel.BasicPublish(
                exchange,
                routingKey,
                properties,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json)));
        }
    }

    private IModel? EnsureChannel()
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        lock (_gate)
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_options.RabbitMq),
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
                };

                _connection = factory.CreateConnection("blog-notifications-publisher");
                _channel = _connection.CreateModel();

                RabbitTopology.Declare(_channel);

                return _channel;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reach RabbitMQ; the delivery stays Pending and is swept later.");

                _channel = null;
                _connection = null;

                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            try
            {
                _channel?.Close();
                _channel?.Dispose();
                _connection?.Close();
                _connection?.Dispose();
            }
            catch
            {
                // Shutting down; a broker that has already gone is not an error.
            }
        }

        GC.SuppressFinalize(this);
    }
}
