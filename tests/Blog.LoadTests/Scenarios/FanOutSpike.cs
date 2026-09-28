using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Npgsql;
using NpgsqlTypes;
using Blog.Contracts;

namespace Blog.LoadTests.Scenarios;

// A post published to a large audience. Times the newsletter fan-out from publish until every
// active subscriber has a delivery claimed and queued, then replays the same post.published event
// and checks that the replay creates nothing. Removes its synthetic subscribers afterwards, so
// later posts do not fan out to them.
public static class FanOutSpike
{
    // The notifications service's default group for the lifecycle topic.
    private const string RelayGroup = "notifications-relay";

    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        var subscribers = options.Int("subscribers", 100_000);

        var summary = new Summary("fanout-spike");

        // The relay announces one post at a time. Seeding the audience while it still has posts to
        // announce (scheduled-burst leaves it 2,000) sends every one of them to the whole audience:
        // one run queued two million emails that way.
        if (!await RelayCaughtUpAsync(settings, TimeSpan.FromMinutes(5)))
        {
            summary.Check("the relay had announced every earlier post before the audience was seeded", false,
                "still behind after 5 minutes; is blog-notifications running?");

            return summary;
        }

        await RemoveSyntheticSubscribersAsync(settings);
        await SeedSubscribersAsync(settings, subscribers);

        var audience = await Db.ScalarAsync(settings.NotificationsDb, "SELECT count(*) FROM subscribers WHERE \"IsActive\"");

        using var api = new BlogApi(settings);

        var admin = await api.LoginAdminAsync();

        var published = DateTime.UtcNow;
        var postId = await api.CreatePostAsync(admin, $"fanout-spike {Guid.NewGuid():N}");

        var created = $"SELECT count(*) FROM notification_deliveries WHERE \"PostId\" = {postId}";
        var queued = $"SELECT count(*) FROM notification_deliveries WHERE \"PostId\" = {postId} AND \"Status\" <> 'Pending'";

        TimeSpan? allClaimed = null;
        TimeSpan? allQueued = null;

        while (allQueued is null && DateTime.UtcNow - published < TimeSpan.FromMinutes(10))
        {
            if (allClaimed is null && await Db.TryScalarAsync(settings.NotificationsDb, created) >= audience)
            {
                allClaimed = DateTime.UtcNow - published;
            }

            if (await Db.TryScalarAsync(settings.NotificationsDb, queued) >= audience)
            {
                allQueued = DateTime.UtcNow - published;
            }

            await Task.Delay(200);
        }

        await ReplayPublishedEventAsync(settings, postId);
        await Task.Delay(TimeSpan.FromSeconds(15));

        var afterReplay = await Db.ScalarAsync(settings.NotificationsDb, created);

        summary.Line($"| active subscribers | {audience} |");
        summary.Line($"| every delivery claimed | {Seconds(allClaimed)} after publishing |");
        summary.Line($"| every delivery queued | {Seconds(allQueued)} after publishing |");
        summary.Line(allQueued is { } q ? $"| fan-out rate | {audience / q.TotalSeconds:0} deliveries/s |" : "| fan-out rate | n/a |");
        summary.Line($"| deliveries after replaying the event | {afterReplay} |");

        summary.Check("every active subscriber got exactly one delivery", afterReplay == audience, $"{afterReplay} / {audience}");
        summary.Check("fan-out finished within 10 minutes", allQueued is not null, Seconds(allQueued));

        await RemoveSyntheticSubscribersAsync(settings);

        return summary;
    }

    private static async Task<bool> RelayCaughtUpAsync(LoadTestSettings settings, TimeSpan timeout)
    {
        var partitions = Enumerable.Range(0, Topics.Partitions[Topics.PostLifecycle])
            .Select(partition => new TopicPartition(Topics.PostLifecycle, partition))
            .ToList();

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = settings.Kafka,
            BrokerAddressFamily = BrokerAddressFamily.V4
        }).Build();

        using var watermarks = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = settings.Kafka,
            GroupId = "loadtest-watermarks",
            BrokerAddressFamily = BrokerAddressFamily.V4
        }).Build();

        var caughtUp = await Db.WaitUntilAsync(async () =>
        {
            var committed = (await admin.ListConsumerGroupOffsetsAsync(new[]
            {
                new ConsumerGroupTopicPartitions(RelayGroup, partitions)
            })).Single().Partitions;

            // A partition the group never committed on counts from its start.
            var lag = committed.Sum(partition =>
                watermarks.QueryWatermarkOffsets(partition.TopicPartition, TimeSpan.FromSeconds(10)).High.Value
                - Math.Max(partition.Offset.Value, 0));

            return lag == 0;
        }, timeout);

        return caughtUp is not null;
    }

    private static async Task SeedSubscribersAsync(LoadTestSettings settings, int count)
    {
        await using var connection = new NpgsqlConnection(settings.NotificationsDb);
        await connection.OpenAsync();

        await using var writer = await connection.BeginBinaryImportAsync(
            """COPY subscribers ("Id", "Email", "IsActive", "SubscribedAt", "UnsubscribeToken") FROM STDIN (FORMAT BINARY)""");

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();

            await writer.StartRowAsync();
            await writer.WriteAsync(id, NpgsqlDbType.Uuid);
            await writer.WriteAsync($"loadtest-{id:N}@example.test", NpgsqlDbType.Varchar);
            await writer.WriteAsync(true, NpgsqlDbType.Boolean);
            await writer.WriteAsync(DateTime.UtcNow, NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(id.ToString("N"), NpgsqlDbType.Varchar);
        }

        await writer.CompleteAsync();

        await Db.ExecAsync(settings.NotificationsDb, "ANALYZE subscribers");
    }

    private static Task RemoveSyntheticSubscribersAsync(LoadTestSettings settings) =>
        Db.ExecAsync(settings.NotificationsDb, """
            DELETE FROM notification_deliveries
            WHERE "SubscriberId" IN (SELECT "Id" FROM subscribers WHERE "Email" LIKE 'loadtest-%');
            DELETE FROM subscribers WHERE "Email" LIKE 'loadtest-%';
            """);

    // Sends the post.published event again, byte for byte as the outbox stored it, the way a
    // redelivery after a consumer crash would look.
    private static async Task ReplayPublishedEventAsync(LoadTestSettings settings, int postId)
    {
        await using var connection = new NpgsqlConnection(settings.PostsDb);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"""
            SELECT "Payload"::text, "SchemaVersion" FROM outbox_messages
            WHERE "PartitionKey" = '{postId}' AND "EventType" = 'post.published'
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return;
        }

        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig
            {
                BootstrapServers = settings.Kafka,

                // The broker advertises localhost to host clients; see LoadTestSettings on why not ::1.
                BrokerAddressFamily = BrokerAddressFamily.V4
            }).Build();

        await producer.ProduceAsync(Topics.PostLifecycle, new Message<string, byte[]>
        {
            Key = postId.ToString(),
            Value = Encoding.UTF8.GetBytes(reader.GetString(0)),
            Headers = new Headers
            {
                { EventHeaders.EventType, Encoding.UTF8.GetBytes("post.published") },
                { EventHeaders.SchemaVersion, Encoding.UTF8.GetBytes(reader.GetInt32(1).ToString()) }
            }
        });
    }

    private static string Seconds(TimeSpan? value) => value is { } v ? $"{v.TotalSeconds:0.0} s" : "not reached";
}
