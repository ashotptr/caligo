using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Caligo.Notifications.Core;

namespace Caligo.Notifications.Host.Messaging;

public class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public string RabbitMq { get; set; } = string.Empty;

    public bool RabbitConfigured => !string.IsNullOrWhiteSpace(RabbitMq);

    public string KafkaBootstrapServers { get; set; } = string.Empty;

    public bool KafkaConfigured => !string.IsNullOrWhiteSpace(KafkaBootstrapServers);

    public string RelayGroup { get; set; } = "notifications-relay";

    public ushort Prefetch { get; set; } = 10;

    public string SiteUrl { get; set; } = "http://localhost:8080";

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan StrandedAfter { get; set; } = TimeSpan.FromMinutes(2);

    public int FanOutChunkSize { get; set; } = 5000;
}

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

    public Task<bool> EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Publish(RabbitTopology.Exchange, RabbitTopology.EmailRoutingKey, command));

    public Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default)
    {
        var nextAttempt = command.Attempt + 1;

        if (nextAttempt >= RabbitTopology.MaxAttempts)
        {
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

        Publish(
            RabbitTopology.RetryExchange,
            RabbitTopology.RetryRoutingKey(command.Attempt),
            command with { Attempt = nextAttempt },
            error);

        return Task.CompletedTask;
    }

    private bool Publish(string exchange, string routingKey, SendEmailCommand command, string? error = null)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var channel = EnsureChannel();

        if (channel is null)
        {
            return false;
        }

        var properties = channel.CreateBasicProperties();

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

        return true;
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

                _connection = factory.CreateConnection("caligo-notifications-publisher");
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
            }
        }

        GC.SuppressFinalize(this);
    }
}
