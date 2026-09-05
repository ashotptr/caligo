using RabbitMQ.Client;

namespace Blog.Notifications.Host.Messaging;

public static class RabbitTopology
{
    public const string Exchange = "blog.notifications";
    public const string RetryExchange = "blog.notifications.retry";
    public const string DeadLetterExchange = "blog.notifications.dlq";

    public const string EmailQueue = "blog.notifications.email";
    public const string DeadLetterQueue = "blog.notifications.dlq";

    public const string EmailRoutingKey = "email.post-published";

    public static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5)
    };

    public static int MaxAttempts => RetryDelays.Length + 1;

    public static string RetryQueue(int attempt) => $"blog.notifications.retry.{Label(attempt)}";

    public static string RetryRoutingKey(int attempt) => $"retry.{Label(attempt)}";

    private static string Label(int attempt)
    {
        var delay = RetryDelays[Math.Clamp(attempt, 0, RetryDelays.Length - 1)];

        return delay < TimeSpan.FromMinutes(1)
            ? $"{(int)delay.TotalSeconds}s"
            : $"{(int)delay.TotalMinutes}m";
    }

    public static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(Exchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(RetryExchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(DeadLetterExchange, ExchangeType.Fanout, durable: true);

        channel.QueueDeclare(
            EmailQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange
            });

        channel.QueueBind(EmailQueue, Exchange, EmailRoutingKey);

        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            var queue = RetryQueue(attempt);

            channel.QueueDeclare(
                queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object>
                {
                    ["x-message-ttl"] = (int)RetryDelays[attempt].TotalMilliseconds,

                    ["x-dead-letter-exchange"] = Exchange,
                    ["x-dead-letter-routing-key"] = EmailRoutingKey
                });

            channel.QueueBind(queue, RetryExchange, RetryRoutingKey(attempt));
        }

        channel.QueueDeclare(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(DeadLetterQueue, DeadLetterExchange, string.Empty);
    }
}
