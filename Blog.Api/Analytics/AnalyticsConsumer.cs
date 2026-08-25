using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Blog.Api.Analytics
{
    public class AnalyticsConsumer : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AnalyticsConsumer> _logger;

        private IConnection? _connection;
        private IModel? _channel;

        public AnalyticsConsumer(IServiceProvider services, IConfiguration configuration, ILogger<AnalyticsConsumer> logger)
        {
            _services = services;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var connectionString = _configuration.GetConnectionString("RabbitMq");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogInformation("No RabbitMq connection string, analytics consumer not started.");

                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Connect(connectionString);

                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Analytics consumer disconnected, retrying in 10s.");

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

        private void Connect(string connectionString)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
            };

            _connection = factory.CreateConnection("blog-analytics-consumer");
            _channel = _connection.CreateModel();

            _channel.ExchangeDeclare(RabbitMqAnalyticsPublisher.ExchangeName, ExchangeType.Topic, durable: true);
            _channel.QueueDeclare(RabbitMqAnalyticsPublisher.QueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueBind(RabbitMqAnalyticsPublisher.QueueName, RabbitMqAnalyticsPublisher.ExchangeName, RabbitMqAnalyticsPublisher.RoutingKey);

            _channel.BasicQos(prefetchSize: 0, prefetchCount: 20, global: false);

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += OnReceived;

            _channel.BasicConsume(RabbitMqAnalyticsPublisher.QueueName, autoAck: false, consumer: consumer);

            _logger.LogInformation("Analytics consumer listening on {Queue}.", RabbitMqAnalyticsPublisher.QueueName);
        }

        private void OnReceived(object? sender, BasicDeliverEventArgs args)
        {
            PostViewedEvent? viewed;

            try
            {
                viewed = JsonSerializer.Deserialize<PostViewedEvent>(Encoding.UTF8.GetString(args.Body.ToArray()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unparseable analytics message discarded.");
                
                _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: false);

                return;
            }

            if (viewed is null)
            {
                _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: false);

                return;
            }

            try
            {
                using var scope = _services.CreateScope();

                var store = scope.ServiceProvider.GetRequiredService<IViewStore>();

                store.RecordAsync(new PostView
                {
                    PostId = viewed.PostId,
                    ViewedAt = viewed.ViewedAt,
                    Referrer = viewed.Referrer,
                    UserId = viewed.UserId,
                    VisitorHash = viewed.VisitorHash
                }).GetAwaiter().GetResult();

                _channel?.BasicAck(args.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not store view; requeueing.");

                _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
            }
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
}
