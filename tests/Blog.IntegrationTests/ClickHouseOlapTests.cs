using System.Text;
using System.Text.Json;
using ClickHouse.Client.ADO;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Olap;
using Blog.Analytics.Core.Views;
using Blog.Analytics.Infrastructure.Stores;
using Blog.Contracts;
using Blog.Contracts.Events;
using Xunit;

namespace Blog.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class ClickHouseOlapTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InfrastructureFixture _infrastructure;
    private ClickHouseOlapStore _store = null!;

    public ClickHouseOlapTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        _store = new ClickHouseOlapStore(
            Options.Create(new ClickHouseOptions { ConnectionString = _infrastructure.ClickHouseConnectionString }),
            NullLogger<ClickHouseOlapStore>.Instance);

        await _store.EnsureSchemaAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // InitializeAsync has already applied the schema for this and every earlier test; the journal
    // must still hold each embedded script exactly once.
    [Fact]
    public async Task ApplyingTheSchemaAgain_RunsNoScriptTwice()
    {
        await _store.EnsureSchemaAsync();

        var scripts = typeof(ClickHouseOlapStore).Assembly.GetManifestResourceNames()
            .Count(name => name.StartsWith("ClickHouseMigrations.", StringComparison.Ordinal));

        Assert.True(scripts > 0);
        Assert.Equal(scripts, await ScalarAsync("SELECT count() FROM schemaversions"));
    }

    [Fact]
    public async Task WritingTheSameBatchTwice_StoresItOnce_InTheRawTableAndTheRollup()
    {
        var postId = NewPostId();
        var views = Enumerable.Range(0, 25).Select(i => View(postId, $"visitor-{i % 5}", DateTime.UtcNow)).ToList();

        await _store.WriteViewsAsync(views, $"test:{postId}:0-24");
        await _store.WriteViewsAsync(views, $"test:{postId}:0-24");

        Assert.Equal(25, await ScalarAsync($"SELECT count() FROM post_views WHERE post_id = {postId}"));
        Assert.Equal(25, await ScalarAsync($"SELECT sum(views) FROM post_views_1m WHERE post_id = {postId}"));
    }

    [Fact]
    public async Task ASeries_BucketsViewsAndClapsTogether()
    {
        var postId = NewPostId();
        var hour = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

        await _store.WriteViewsAsync(new[]
        {
            View(postId, "a", hour.AddMinutes(1)),
            View(postId, "b", hour.AddMinutes(1).AddSeconds(30)),
            View(postId, "a", hour.AddMinutes(2)),
            View(postId, "c", hour.AddMinutes(61))
        }, $"series-views-{postId}");

        await _store.WriteClapsAsync(new[]
        {
            Clap(postId, "reader-1", 10, hour.AddMinutes(2)),
            Clap(postId, "reader-2", 5, hour.AddMinutes(62))
        }, $"series-claps-{postId}");

        var minutes = await _store.SeriesAsync(postId, hour, hour.AddHours(2), SeriesBucket.Minute);

        Assert.Equal(
            new[]
            {
                new SeriesPoint(hour.AddMinutes(1), 2, 2, 0),
                new SeriesPoint(hour.AddMinutes(2), 1, 1, 10),
                new SeriesPoint(hour.AddMinutes(61), 1, 1, 0),
                new SeriesPoint(hour.AddMinutes(62), 0, 0, 5)
            },
            minutes);

        var hours = await _store.SeriesAsync(postId, hour, hour.AddHours(2), SeriesBucket.Hour);

        Assert.Equal(
            new[]
            {
                new SeriesPoint(hour, 3, 2, 10),
                new SeriesPoint(hour.AddHours(1), 1, 1, 5)
            },
            hours);
    }

    [Fact]
    public async Task TheSlidingWindow_RanksByWhicheverMetricIsAskedFor()
    {
        var viewed = NewPostId();
        var clapped = NewPostId();
        var now = DateTime.UtcNow;

        await _store.WriteViewsAsync(
            Enumerable.Range(0, 400).Select(i => View(viewed, $"v{i}", now)).ToList(), $"window-views-{viewed}");

        await _store.WriteClapsAsync(
            Enumerable.Range(0, 30).Select(i => Clap(clapped, $"r{i}", 50, now)).ToList(), $"window-claps-{clapped}");

        var byViews = await _store.TopInWindowAsync(5, RankBy.Views, 50);
        var byClaps = await _store.TopInWindowAsync(5, RankBy.Claps, 50);

        Assert.Equal(viewed, byViews.First(e => e.PostId == viewed || e.PostId == clapped).PostId);
        Assert.Equal(clapped, byClaps.First(e => e.PostId == viewed || e.PostId == clapped).PostId);
        Assert.Equal(1500, byClaps.Single(e => e.PostId == clapped).Claps);
    }

    [Fact]
    public async Task TheFunnel_JoinsViewsReadersAndClappers()
    {
        var postId = NewPostId();
        var now = DateTime.UtcNow;

        await _store.WriteViewsAsync(new[]
        {
            View(postId, "a", now), View(postId, "a", now), View(postId, "b", now),
            View(postId, "c", now), View(postId, "d", now)
        }, $"funnel-views-{postId}");

        await _store.WriteClapsAsync(new[]
        {
            Clap(postId, "reader-1", 20, now), Clap(postId, "reader-1", 5, now), Clap(postId, "reader-2", 1, now)
        }, $"funnel-claps-{postId}");

        var funnel = await _store.FunnelAsync(postId, 7);

        Assert.Equal(new EngagementFunnel(postId, 5, 4, 2, 26), funnel);
    }

    [Fact]
    public async Task EventsPublishedToKafka_ReachClickHouse_ThroughTheOlapConsumerGroups()
    {
        await EnsureTopicsAsync();

        using var factory = new AnalyticsHostFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = _infrastructure.KafkaBootstrapServers,
            ["Kafka:BatchLinger"] = "00:00:00.200",
            ["ConnectionStrings:ClickHouse"] = _infrastructure.ClickHouseConnectionString
        });

        factory.CreateClient();

        var postId = NewPostId();

        using (var producer = new ProducerBuilder<string, byte[]>(
                   new ProducerConfig { BootstrapServers = _infrastructure.KafkaBootstrapServers }).Build())
        {
            for (var i = 0; i < 40; i++)
            {
                await ProduceAsync(producer, Topics.PostViewed, new PostViewedEvent
                {
                    PostId = postId,
                    VisitorHash = $"visitor-{i % 8}"
                });
            }

            for (var i = 0; i < 6; i++)
            {
                await ProduceAsync(producer, Topics.PostClapped, new PostClappedEvent
                {
                    PostId = postId,
                    UserId = $"reader-{i}",
                    Delta = 7,
                    ReaderTotal = 7,
                    FirstClap = true
                });
            }
        }

        var landed = await WaitForAsync(async () =>
            await ScalarAsync($"SELECT count() FROM post_views WHERE post_id = {postId}") == 40
            && await ScalarAsync($"SELECT count() FROM clap_events WHERE post_id = {postId}") == 6,
            TimeSpan.FromSeconds(90));

        Assert.True(landed, "the OLAP consumer groups did not deliver every event to ClickHouse");

        var funnel = await _store.FunnelAsync(postId, 1);

        Assert.Equal(new EngagementFunnel(postId, 40, 8, 6, 42), funnel);
    }

    private static int NewPostId() => Random.Shared.Next(1_000_000, int.MaxValue);

    private static PostView View(int postId, string visitor, DateTime at) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        PostId = postId,
        ViewedAt = at,
        VisitorHash = visitor
    };

    private static ClapEvent Clap(int postId, string userId, int delta, DateTime at) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        PostId = postId,
        UserId = userId,
        Delta = delta,
        ClappedAt = at
    };

    private async Task<long> ScalarAsync(string sql)
    {
        using var connection = new ClickHouseConnection(_infrastructure.ClickHouseConnectionString);
        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task EnsureTopicsAsync()
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _infrastructure.KafkaBootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(new[] { Topics.PostViewed, Topics.PostClapped }.Select(topic =>
                new TopicSpecification { Name = topic, NumPartitions = Topics.Partitions[topic], ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r =>
            r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private static Task ProduceAsync<T>(IProducer<string, byte[]> producer, string topic, T message)
        where T : IntegrationEvent =>
        producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = message.PartitionKey,
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, message.GetType(), Json)),
            Headers = new Headers
            {
                { EventHeaders.EventType, Encoding.UTF8.GetBytes(message.EventType) }
            }
        });

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
}
