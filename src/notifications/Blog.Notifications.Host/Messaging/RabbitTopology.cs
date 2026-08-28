using RabbitMQ.Client;

namespace Blog.Notifications.Host.Messaging;

/// <summary>
/// The RabbitMQ topology, declared in one place so the publisher and the worker cannot
/// disagree about it.
///
/// The shape is what RabbitMQ is actually for, and what Kafka has no answer to:
///
///   blog.notifications ──▶ blog.notifications.email        (workers compete for messages)
///                                    │ send fails
///                                    ▼
///   blog.notifications.retry ──▶ retry.5s / retry.30s / retry.5m
///                                    │ message TTL expires
///                                    ▼  dead-letters back to the main exchange
///   blog.notifications ──▶ blog.notifications.email        (tried again)
///                                    │ attempts exhausted
///                                    ▼
///   blog.notifications.dlq ──▶ blog.notifications.dlq      (parked for a human)
///
/// The delay comes from per-queue message TTL plus dead-lettering: a message sits in the 30s
/// queue doing nothing, and when its TTL expires RabbitMQ routes it back to where it came
/// from. That gives real exponential backoff with no plugin and no timer in the worker.
///
/// A rejected message is republished to the retry exchange explicitly rather than nacked,
/// because a queue's dead-letter exchange is fixed at declaration time - nacking could only
/// ever produce one delay, not a ladder of them.
/// </summary>
public static class RabbitTopology
{
    public const string Exchange = "blog.notifications";
    public const string RetryExchange = "blog.notifications.retry";
    public const string DeadLetterExchange = "blog.notifications.dlq";

    public const string EmailQueue = "blog.notifications.email";
    public const string DeadLetterQueue = "blog.notifications.dlq";

    public const string EmailRoutingKey = "email.post-published";

    /// <summary>
    /// How long each attempt waits before the next. Also the number of attempts: a message that
    /// exhausts the ladder is parked rather than retried forever.
    /// </summary>
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

    /// <summary>
    /// Declares everything. Idempotent, so every instance can run it at startup - which is what
    /// stops a worker from consuming a queue whose bindings nobody created yet.
    /// </summary>
    public static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(Exchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(RetryExchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(DeadLetterExchange, ExchangeType.Fanout, durable: true);

        // The working queue. It carries no TTL: a message waits here until a worker takes it.
        channel.QueueDeclare(
            EmailQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                // Where an expired or rejected message goes if one ever is, which the worker
                // does not rely on but which stops anything vanishing.
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
                    // Nothing consumes these queues. A message expires where it lies, and
                    // expiry is what moves it on - the delay is the whole purpose of the queue.
                    ["x-message-ttl"] = (int)RetryDelays[attempt].TotalMilliseconds,

                    // On expiry, back to the main exchange under the working routing key.
                    ["x-dead-letter-exchange"] = Exchange,
                    ["x-dead-letter-routing-key"] = EmailRoutingKey
                });

            channel.QueueBind(queue, RetryExchange, RetryRoutingKey(attempt));
        }

        // Parked messages. Nothing consumes this either: it is a queue of things a human needs
        // to look at, visible in the management UI, and deliberately not a silent drop.
        channel.QueueDeclare(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(DeadLetterQueue, DeadLetterExchange, string.Empty);
    }
}
