using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Blog.Notifications.Core;
using Blog.Notifications.Host.Messaging;
using Blog.Notifications.Host.Persistence;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

[Collection(InfrastructureCollection.Name)]
public class NotificationsConcurrencyTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;

    public NotificationsConcurrencyTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        await using var context = Context();

        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoRelaysFanningOutTheSameEvent_CreateOneDeliveryPerActiveSubscriber()
    {
        var eventId = Guid.NewGuid();
        var active = await SeedSubscribersAsync(active: 12_345, inactive: 3_000);

        var queue = new CountingQueue();

        var fanOuts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var context = Context();

            return await FanOut(context, queue).RunAsync(eventId, 1, "Big launch");
        })));

        Assert.Equal(active.Count, fanOuts.Sum(f => f.Claimed));
        Assert.Equal(active.Count, queue.Commands.Count);
        Assert.Equal(active.Count, queue.Commands.Select(c => c.SubscriberId).Distinct().Count());
        Assert.True(queue.Commands.All(c => active.Contains(c.SubscriberId)));

        await using var verify = Context();

        Assert.Equal(active.Count, await verify.Deliveries.CountAsync(d => d.EventId == eventId));

        var replay = await FanOut(verify, queue).RunAsync(eventId, 1, "Big launch");

        Assert.Equal(new FanOutResult(0, 0), replay);
    }

    [Fact]
    public async Task OnlyOneSweeperAtATime_GetsTheLock()
    {
        await using var firstContext = Context();
        await using var secondContext = Context();

        var first = new EfDeliveryLedger(firstContext);
        var second = new EfDeliveryLedger(secondContext);

        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var holder = first.RunExclusiveAsync(StrandedDeliverySweeper.SweepLock, async () =>
        {
            inside.SetResult();

            await release.Task;
        });

        await inside.Task;

        var ranSecond = false;

        var contender = await second.RunExclusiveAsync(StrandedDeliverySweeper.SweepLock, () =>
        {
            ranSecond = true;

            return Task.CompletedTask;
        });

        release.SetResult();

        Assert.True(await holder);
        Assert.False(contender);
        Assert.False(ranSecond);

        Assert.True(await second.RunExclusiveAsync(StrandedDeliverySweeper.SweepLock, () => Task.CompletedTask));
    }

    [Fact]
    public async Task TwentyConcurrentSubscribesWithOneAddress_AllSucceed_AndLeaveOneRow()
    {
        var email = $"{Guid.NewGuid():N}@example.test";

        var subscribers = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await using var context = Context();

            return await new EfSubscriberStore(context).SubscribeAsync(email.ToUpperInvariant(), $"Reader {i}");
        })));

        Assert.Single(subscribers.Select(s => s.Id).Distinct());

        await using var verify = Context();

        Assert.Equal(1, await verify.Subscribers.CountAsync(s => s.Email == email));
    }

    [Fact]
    public async Task ASentDelivery_IsNeverDraggedBackToFailed()
    {
        await using var context = Context();

        var delivery = new NotificationDelivery
        {
            EventId = Guid.NewGuid(),
            SubscriberId = Guid.NewGuid(),
            PostId = 1,
            PostTitle = "t",
            Status = DeliveryStatus.Queued
        };

        context.Deliveries.Add(delivery);
        await context.SaveChangesAsync();

        var ledger = new EfDeliveryLedger(context);

        await ledger.MarkSentAsync(delivery.Id);
        await ledger.MarkFailedAsync(delivery.Id, 4, "a late retry of an older copy");

        var stored = await context.Deliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);

        Assert.Equal(DeliveryStatus.Sent, stored.Status);
    }

    private async Task<HashSet<Guid>> SeedSubscribersAsync(int active, int inactive)
    {
        var ids = new HashSet<Guid>();

        await using var connection = new NpgsqlConnection(_infrastructure.NotificationsConnectionString);
        await connection.OpenAsync();

        // Earlier tests may have left subscribers behind; this one needs to know the whole audience.
        await using (var clear = new NpgsqlCommand("DELETE FROM notification_deliveries; DELETE FROM subscribers", connection))
        {
            await clear.ExecuteNonQueryAsync();
        }

        await using var writer = await connection.BeginBinaryImportAsync(
            """COPY subscribers ("Id", "Email", "DisplayName", "IsActive", "SubscribedAt", "UnsubscribeToken") FROM STDIN (FORMAT BINARY)""");

        for (var i = 0; i < active + inactive; i++)
        {
            var id = Guid.NewGuid();
            var isActive = i < active;

            if (isActive)
            {
                ids.Add(id);
            }

            await writer.StartRowAsync();
            await writer.WriteAsync(id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync($"{id:N}@example.test", NpgsqlTypes.NpgsqlDbType.Varchar);
            await writer.WriteNullAsync();
            await writer.WriteAsync(isActive, NpgsqlTypes.NpgsqlDbType.Boolean);
            await writer.WriteAsync(DateTime.UtcNow, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(id.ToString("N"), NpgsqlTypes.NpgsqlDbType.Varchar);
        }

        await writer.CompleteAsync();

        return ids;
    }

    private NotificationsDbContext Context() => new(
        new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(_infrastructure.NotificationsConnectionString)
            .Options);

    private static NewsletterFanOut FanOut(NotificationsDbContext context, ICommandQueue queue) => new(
        new EfDeliveryLedger(context),
        queue,
        Options.Create(new NotificationsOptions { FanOutChunkSize = 1_000 }));

    private sealed class CountingQueue : ICommandQueue
    {
        public ConcurrentBag<SendEmailCommand> Commands { get; } = new();

        public bool IsConfigured => true;

        public Task<bool> EnqueueAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return Task.FromResult(true);
        }

        public Task RetryOrParkAsync(SendEmailCommand command, string error, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
