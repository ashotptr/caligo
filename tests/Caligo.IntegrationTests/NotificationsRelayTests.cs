using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Caligo.Contracts;
using Caligo.Contracts.Events;
using Caligo.Notifications.Core;
using Caligo.Notifications.Host.Messaging;
using Caligo.Notifications.Host.Persistence;
using Xunit;

namespace Caligo.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class NotificationsRelayTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InfrastructureFixture _infrastructure;

    public NotificationsRelayTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        await using var context = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(_infrastructure.NotificationsConnectionString)
                .Options);

        await context.Database.MigrateAsync();

        await EnsureTopicAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnEventWhoseFanOutFailed_IsRelayedAgain_NotSkippedByTheNextOne()
    {
        var subscriber = await SeedSubscriberAsync();

        var postId = 900_000_000 + Random.Shared.Next(1_000_000);
        var failing = new PostPublishedEvent { PostId = postId, Title = "Fails once" };
        var following = new PostPublishedEvent { PostId = postId + 1, Title = "Goes through" };

        var group = $"relay-test-{Guid.NewGuid():N}";

        StartGroupAtTheEnd(group);

        var services = new ServiceCollection();

        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNpgsql(_infrastructure.NotificationsConnectionString));
        services.AddSingleton(new ClaimFailure(failing.PostId));
        services.AddScoped<IDeliveryLedger>(provider => new FailingOnceLedger(
            new EfDeliveryLedger(provider.GetRequiredService<NotificationsDbContext>()),
            provider.GetRequiredService<ClaimFailure>()));
        services.AddSingleton<ICommandQueue, AcceptingQueue>();
        services.AddScoped<NewsletterFanOut>();
        services.Configure<NotificationsOptions>(options =>
        {
            options.KafkaBootstrapServers = _infrastructure.KafkaBootstrapServers;
            options.RelayGroup = group;
            options.FanOutChunkSize = 5_000;
        });

        await using var provider = services.BuildServiceProvider();

        var relay = new PostLifecycleRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<NotificationsOptions>>(),
            NullLogger<PostLifecycleRelay>.Instance);

        await PublishToOnePartitionAsync(failing, following);

        await relay.StartAsync(CancellationToken.None);

        try
        {
            var bothAnnounced = await WaitForAsync(
                async () => await DeliveriesAsync(failing.PostId, subscriber) == 1
                    && await DeliveriesAsync(following.PostId, subscriber) == 1,
                TimeSpan.FromSeconds(60));

            Assert.True(bothAnnounced, "Both posts should reach the subscriber, the failed one after its retry.");
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    private async Task EnsureTopicAsync()
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _infrastructure.KafkaBootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(new[]
            {
                new TopicSpecification
                {
                    Name = Topics.PostLifecycle,
                    NumPartitions = Topics.Partitions[Topics.PostLifecycle],
                    ReplicationFactor = 1
                }
            });
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r =>
            r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private void StartGroupAtTheEnd(string group)
    {
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = _infrastructure.KafkaBootstrapServers,
            GroupId = group,
            EnableAutoCommit = false
        }).Build();

        var ends = Enumerable.Range(0, Topics.Partitions[Topics.PostLifecycle])
            .Select(partition => new TopicPartition(Topics.PostLifecycle, partition))
            .Select(partition => new TopicPartitionOffset(
                partition, consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High))
            .ToList();

        consumer.Assign(ends);
        consumer.Commit(ends);
        consumer.Close();
    }

    private async Task PublishToOnePartitionAsync(params PostPublishedEvent[] events)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = _infrastructure.KafkaBootstrapServers }).Build();

        foreach (var published in events)
        {
            await producer.ProduceAsync(new TopicPartition(Topics.PostLifecycle, 0), new Message<string, byte[]>
            {
                Key = published.PartitionKey,
                Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(published, Json)),
                Headers = new Headers
                {
                    { EventHeaders.EventType, Encoding.UTF8.GetBytes(published.EventType) }
                }
            });
        }
    }

    private async Task<Guid> SeedSubscriberAsync()
    {
        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_infrastructure.NotificationsConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            INSERT INTO subscribers ("Id", "Email", "IsActive", "SubscribedAt", "UnsubscribeToken")
            VALUES (@id, @email, true, now(), @token)
            """, connection);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", $"relay-{id:N}@example.test");
        command.Parameters.AddWithValue("token", id.ToString("N"));

        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<long> DeliveriesAsync(int postId, Guid subscriberId)
    {
        await using var connection = new NpgsqlConnection(_infrastructure.NotificationsConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """SELECT count(*) FROM notification_deliveries WHERE "PostId" = @post AND "SubscriberId" = @subscriber""",
            connection);

        command.Parameters.AddWithValue("post", postId);
        command.Parameters.AddWithValue("subscriber", subscriberId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private sealed class ClaimFailure
    {
        private readonly int _postId;
        private int _tripped;

        public ClaimFailure(int postId)
        {
            _postId = postId;
        }

        public bool Trip(int postId) => postId == _postId && Interlocked.Exchange(ref _tripped, 1) == 0;
    }

    private sealed class FailingOnceLedger : IDeliveryLedger
    {
        private readonly IDeliveryLedger _inner;
        private readonly ClaimFailure _failure;

        public FailingOnceLedger(IDeliveryLedger inner, ClaimFailure failure)
        {
            _inner = inner;
            _failure = failure;
        }

        public Task<FanOutChunk> ClaimChunkAsync(
            Guid eventId, int postId, string postTitle, Guid after, int chunkSize,
            CancellationToken cancellationToken = default) =>
            _failure.Trip(postId)
                ? throw new TimeoutException("The database did not answer in time.")
                : _inner.ClaimChunkAsync(eventId, postId, postTitle, after, chunkSize, cancellationToken);

        public Task MarkQueuedAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken cancellationToken = default) =>
            _inner.MarkQueuedAsync(deliveryIds, cancellationToken);

        public Task MarkSentAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
            _inner.MarkSentAsync(deliveryId, cancellationToken);

        public Task MarkFailedAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default) =>
            _inner.MarkFailedAsync(deliveryId, attempts, error, cancellationToken);

        public Task RecordAttemptAsync(Guid deliveryId, int attempts, string error, CancellationToken cancellationToken = default) =>
            _inner.RecordAttemptAsync(deliveryId, attempts, error, cancellationToken);

        public Task<IReadOnlyList<NotificationDelivery>> StrandedAsync(
            TimeSpan olderThan, int limit, CancellationToken cancellationToken = default) =>
            _inner.StrandedAsync(olderThan, limit, cancellationToken);

        public Task<Subscriber?> SubscriberAsync(Guid subscriberId, CancellationToken cancellationToken = default) =>
            _inner.SubscriberAsync(subscriberId, cancellationToken);

        public Task<bool> RunExclusiveAsync(string lockName, Func<Task> work, CancellationToken cancellationToken = default) =>
            _inner.RunExclusiveAsync(lockName, work, cancellationToken);
    }

    private sealed class AcceptingQueue : ICommandQueue
    {
        public bool IsConfigured => true;

        public Task<bool> EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
