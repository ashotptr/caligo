using Microsoft.Extensions.DependencyInjection;
using Caligo.Analytics.Core.Views;
using Caligo.Analytics.Infrastructure.Stores;
using Xunit;

namespace Caligo.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class ViewIngestionTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private AnalyticsHostFactory _factory = null!;

    public ViewIngestionTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _factory = new AnalyticsHostFactory(_infrastructure);
        _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        return Task.CompletedTask;
    }

    private IViewStore Store() => _factory.Services.GetRequiredService<IViewStore>();

    [Fact]
    public async Task RedeliveringTheSameEvent_DoesNotCountItTwice()
    {
        var store = Store();
        var postId = Random.Shared.Next(100_000, 999_999);
        var eventId = Guid.NewGuid().ToString();

        var view = new PostView
        {
            EventId = eventId,
            PostId = postId,
            ViewedAt = DateTime.UtcNow,
            VisitorHash = "visitor-1"
        };

        await store.RecordAsync(view);

        await store.RecordAsync(new PostView
        {
            EventId = eventId,
            PostId = postId,
            ViewedAt = DateTime.UtcNow,
            VisitorHash = "visitor-1"
        });

        var count = await store.ForPostAsync(postId);

        Assert.Equal(1, count!.Views);
    }

    [Fact]
    public async Task ABatchContainingDuplicates_StoresOnlyTheNewOnes()
    {
        var store = Store();
        var postId = Random.Shared.Next(100_000, 999_999);

        var alreadySeen = NewView(postId, "visitor-a");

        await store.RecordAsync(alreadySeen);

        var inserted = await store.RecordManyAsync(new[]
        {
            alreadySeen,
            NewView(postId, "visitor-b"),
            NewView(postId, "visitor-c")
        });

        Assert.Equal(2, inserted);

        var count = await store.ForPostAsync(postId);

        Assert.Equal(3, count!.Views);
        Assert.Equal(3, count.UniqueVisitors);
    }

    [Fact]
    public async Task UniqueVisitors_AreCountedByHash_NotByView()
    {
        var store = Store();
        var postId = Random.Shared.Next(100_000, 999_999);

        await store.RecordManyAsync(Enumerable.Range(0, 4).Select(_ => NewView(postId, "same-visitor")).ToList());

        var count = await store.ForPostAsync(postId);

        Assert.Equal(4, count!.Views);
        Assert.Equal(1, count.UniqueVisitors);
    }

    [Fact]
    public async Task APostWithNoViews_ReportsZero_RatherThanNull()
    {
        var count = await Store().ForPostAsync(Random.Shared.Next(1_000_000, 9_999_999));

        Assert.NotNull(count);
        Assert.Equal(0, count!.Views);
    }

    private static PostView NewView(int postId, string visitor) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        PostId = postId,
        ViewedAt = DateTime.UtcNow,
        VisitorHash = visitor
    };
}
