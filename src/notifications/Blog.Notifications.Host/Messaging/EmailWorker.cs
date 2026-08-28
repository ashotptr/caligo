using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

/// <summary>
/// Executes email commands from RabbitMQ.
///
/// Competing consumers: run three of these and RabbitMQ hands each message to exactly one of
/// them. That is the opposite of the Kafka side, where every consumer group sees every event -
/// and it is the right shape here, because an email should be sent once, not once per worker.
/// </summary>
public class EmailWorker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICommandQueue _queue;
    private readonly NotificationsOptions _options;
    private readonly ILogger<EmailWorker> _logger;

    private IConnection? _connection;
    private IModel? _channel;

    public EmailWorker(
        IServiceScopeFactory scopes,
        ICommandQueue queue,
        IOptions<NotificationsOptions> options,
        ILogger<EmailWorker> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RabbitConfigured)
        {
            _logger.LogInformation("No RabbitMQ configured; email worker not started.");

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Connect();

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Email worker disconnected; reconnecting in 10s.");

                Cleanup();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Cleanup();
    }

    private void Connect()
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_options.RabbitMq),
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),

            // Required for AsyncEventingBasicConsumer; without it async handlers are never
            // awaited and the client silently uses the synchronous contract instead.
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection("blog-notifications-worker");
        _channel = _connection.CreateModel();

        RabbitTopology.Declare(_channel);

        // Caps how many messages this worker holds unacknowledged. Without it RabbitMQ pushes
        // the entire queue at whichever consumer connects first, and the others idle.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: _options.Prefetch, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnReceivedAsync;

        _channel.BasicConsume(RabbitTopology.EmailQueue, autoAck: false, consumer: consumer);

        _logger.LogInformation(
            "Email worker consuming {Queue} (prefetch {Prefetch}).", RabbitTopology.EmailQueue, _options.Prefetch);
    }

    private async Task OnReceivedAsync(object? sender, BasicDeliverEventArgs args)
    {
        SendEmailCommand? command;

        try
        {
            command = JsonSerializer.Deserialize<SendEmailCommand>(
                Encoding.UTF8.GetString(args.Body.ToArray()), Json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unreadable email command discarded.");

            // Malformed on the wire; requeueing would spin on it forever. The queue's
            // dead-letter exchange catches it instead of it being dropped.
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: false);

            return;
        }

        if (command is null)
        {
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: false);

            return;
        }

        // The attempt count travels in a header because a message dead-lettered by TTL comes
        // back with its original body: RabbitMQ moves the bytes, it does not rewrite them.
        var attempt = ReadAttempt(args) ?? command.Attempt;
        command = command with { Attempt = attempt };

        using var scope = _scopes.CreateScope();

        var ledger = scope.ServiceProvider.GetRequiredService<IDeliveryLedger>();
        var sender2 = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        try
        {
            await sender2.SendPostPublishedAsync(command);

            await ledger.MarkSentAsync(command.DeliveryId);

            // Acknowledged only once the mail server has taken it.
            _channel?.BasicAck(args.DeliveryTag, multiple: false);

            _logger.LogInformation(
                "Sent post {PostId} announcement to {Email}.", command.PostId, command.Email);
        }
        catch (Exception ex)
        {
            var nextAttempt = command.Attempt + 1;

            if (nextAttempt >= RabbitTopology.MaxAttempts)
            {
                await ledger.MarkFailedAsync(command.DeliveryId, nextAttempt, ex.Message);
            }
            else
            {
                await ledger.RecordAttemptAsync(command.DeliveryId, nextAttempt, ex.Message);
            }

            // Republished onto the ladder rather than nacked. A nack would use the queue's own
            // dead-letter exchange, which is fixed - it could not choose a different delay for
            // the second attempt than for the first.
            await _queue.RetryOrParkAsync(command, ex.Message);

            // Acked because this copy has been dealt with: its successor is already on the
            // retry queue. Nacking as well would put two copies in flight.
            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
    }

    private static int? ReadAttempt(BasicDeliverEventArgs args)
    {
        if (args.BasicProperties?.Headers is null)
        {
            return null;
        }

        if (args.BasicProperties.Headers.TryGetValue("x-attempt", out var value) && value is int attempt)
        {
            return attempt;
        }

        return null;
    }

    private void Cleanup()
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
            // Shutting down.
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }

    public override void Dispose()
    {
        Cleanup();

        base.Dispose();

        GC.SuppressFinalize(this);
    }
}
