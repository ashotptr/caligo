using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Blog.Api.Analytics
{
    public interface IAnalyticsPublisher
    {
        void PublishPostViewed(PostViewedEvent viewed);
    }

    public class RabbitMqAnalyticsPublisher : IAnalyticsPublisher, IDisposable
    {
        public const string ExchangeName = "blog.analytics";
        public const string RoutingKey = "post.viewed";
        public const string QueueName = "blog.analytics.post-viewed";

        private readonly ILogger<RabbitMqAnalyticsPublisher> _logger;
        private readonly object _gate = new();

        private IConnection? _connection;
        private IModel? _channel;
        private bool _disposed;

        private DateTime _quietUntil = DateTime.MinValue;

        public RabbitMqAnalyticsPublisher(IConfiguration configuration, ILogger<RabbitMqAnalyticsPublisher> logger)
        {
            _logger = logger;
            ConnectionString = configuration.GetConnectionString("RabbitMq");
        }

        public string? ConnectionString { get; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

        public void PublishPostViewed(PostViewedEvent viewed)
        {
            if (!IsConfigured || DateTime.UtcNow < _quietUntil)
            {
                return;
            }

            try
            {
                var channel = EnsureChannel();

                if (channel is null)
                {
                    return;
                }

                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(viewed));

                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";

                channel.BasicPublish(ExchangeName, RoutingKey, properties, body);
            }
            catch (Exception ex)
            {
                _quietUntil = DateTime.UtcNow.AddSeconds(30);

                lock (_gate)
                {
                    _channel?.Dispose();
                    _connection?.Dispose();
                    _channel = null;
                    _connection = null;
                }

                _logger.LogWarning(ex,
                    "Analytics publish failed; pausing for 30s. Views are not being recorded.");
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

                var factory = new ConnectionFactory
                {
                    Uri = new Uri(ConnectionString!),
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
                };

                _connection = factory.CreateConnection("blog-api");
                _channel = _connection.CreateModel();

                _channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
                _channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
                _channel.QueueBind(QueueName, ExchangeName, RoutingKey);

                _quietUntil = DateTime.MinValue;
                _logger.LogInformation("Analytics publisher connected to RabbitMQ.");

                return _channel;
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
                _channel?.Dispose();
                _connection?.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    public class NullAnalyticsPublisher : IAnalyticsPublisher
    {
        public void PublishPostViewed(PostViewedEvent viewed)
        {
        }
    }
}
