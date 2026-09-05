using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Net.Client;
using Blog.Analytics.Core.Views;
using Blog.Analytics.Host;
using Blog.Contracts.Analytics;
using Xunit;
using CoreStats = Blog.Analytics.Core.Views.PostStats;

namespace Blog.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class AnalyticsServiceTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;

    private AnalyticsHostFactory _host = null!;
    private GrpcChannel _channel = null!;
    private AnalyticsReader.AnalyticsReaderClient _client = null!;

    public AnalyticsServiceTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _host = new AnalyticsHostFactory(_infrastructure);

        _channel = GrpcChannel.ForAddress(
            _host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });

        _client = new AnalyticsReader.AnalyticsReaderClient(_channel);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _channel.Dispose();
        _host.Dispose();

        return Task.CompletedTask;
    }

    private IViewStore Views => _host.Services.GetRequiredService<IViewStore>();

    private ITrendingStore Trending => _host.Services.GetRequiredService<ITrendingStore>();

    [Fact]
    public async Task GetPostStats_ReturnsWhatWasIngested()
    {
        var postId = Random.Shared.Next(100_000, 999_999);

        await Views.RecordManyAsync(new[]
        {
            View(postId, "visitor-a"),
            View(postId, "visitor-a"),
            View(postId, "visitor-b")
        });

        var stats = await _client.GetPostStatsAsync(new GetPostStatsRequest { PostId = postId });

        Assert.Equal(postId, stats.PostId);
        Assert.Equal(3, stats.Views);
        Assert.Equal(2, stats.UniqueVisitors);
    }

    [Fact]
    public async Task GetPostStats_ForAnUnknownPost_ReturnsZero()
    {
        var stats = await _client.GetPostStatsAsync(
            new GetPostStatsRequest { PostId = Random.Shared.Next(1_000_000, 9_999_999) });

        Assert.Equal(0, stats.Views);
    }

    [Fact]
    public async Task GetPostStatsBatch_AnswersForEveryRequestedPost()
    {
        var withViews = Random.Shared.Next(100_000, 999_999);
        var withNone = Random.Shared.Next(100_000, 999_999);

        await Views.RecordManyAsync(new[] { View(withViews, "visitor-a") });

        var request = new GetPostStatsBatchRequest();
        request.PostIds.AddRange(new[] { withViews, withNone });

        var batch = await _client.GetPostStatsBatchAsync(request);

        Assert.Equal(2, batch.Stats.Count);
        Assert.Equal(1, batch.Stats.Single(s => s.PostId == withViews).Views);
        Assert.Equal(0, batch.Stats.Single(s => s.PostId == withNone).Views);
    }

    [Fact]
    public async Task GetPostStatsBatch_RejectsAnOversizedRequest()
    {
        var request = new GetPostStatsBatchRequest();
        request.PostIds.AddRange(Enumerable.Range(1, 500));

        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            () => _client.GetPostStatsBatchAsync(request).ResponseAsync);

        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task GetTopPosts_RanksByViewCount()
    {
        var popular = Random.Shared.Next(100_000, 999_999);
        var quiet = Random.Shared.Next(100_000, 999_999);

        await Views.RecordManyAsync(
            Enumerable.Range(0, 5).Select(i => View(popular, $"v{i}")).ToList());
        await Views.RecordManyAsync(new[] { View(quiet, "v0") });

        var top = await _client.GetTopPostsAsync(new GetTopPostsRequest { Days = 7, Limit = 50 });

        var popularRank = top.Posts.ToList().FindIndex(p => p.PostId == popular);
        var quietRank = top.Posts.ToList().FindIndex(p => p.PostId == quiet);

        Assert.True(popularRank >= 0 && quietRank >= 0, "both posts should appear");
        Assert.True(popularRank < quietRank, "the more-viewed post should rank higher");
    }

    [Fact]
    public async Task StreamTrending_PushesASnapshotWithoutBeingPolled()
    {
        var postId = Random.Shared.Next(100_000, 999_999);

        await Trending.IncrementManyAsync(
            Enumerable.Range(0, 3).Select(_ => View(postId, "v")).ToList());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var call = _client.StreamTrending(
            new StreamTrendingRequest { WindowHours = 24, Limit = 20 },
            cancellationToken: cancellation.Token);

        var received = await call.ResponseStream.MoveNext(cancellation.Token);

        Assert.True(received, "the server should push a snapshot without being polled");

        var snapshot = call.ResponseStream.Current;

        Assert.Equal(24, snapshot.WindowHours);
        Assert.Contains(snapshot.Entries, e => e.PostId == postId && e.Score >= 3);
    }

    [Fact]
    public async Task Trending_FallsOutOfTheWindow_AsBucketsAge()
    {
        var recent = Random.Shared.Next(100_000, 999_999);
        var old = Random.Shared.Next(100_000, 999_999);

        await Trending.IncrementAsync(recent, DateTime.UtcNow);

        await Trending.IncrementAsync(old, DateTime.UtcNow.AddHours(-5));

        var top = await Trending.TopAsync(windowHours: 1, limit: 50);

        Assert.Contains(top, e => e.PostId == recent);
        Assert.DoesNotContain(top, e => e.PostId == old);

        var wider = await Trending.TopAsync(windowHours: 24, limit: 50);

        Assert.Contains(wider, e => e.PostId == old);
    }

    private static PostView View(int postId, string visitor) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        PostId = postId,
        ViewedAt = DateTime.UtcNow,
        VisitorHash = visitor
    };
}

public class AnalyticsHostFactory : WebApplicationFactory<AnalyticsHostMarker>
{
    private readonly InfrastructureFixture _infrastructure;

    public AnalyticsHostFactory(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mongo"] = _infrastructure.MongoConnectionString,
                ["ConnectionStrings:Redis"] = _infrastructure.RedisConnectionString,

                ["Kafka:BootstrapServers"] = "",

                ["Analytics:TrendingPushInterval"] = "00:00:00.200"
            }));
    }
}
