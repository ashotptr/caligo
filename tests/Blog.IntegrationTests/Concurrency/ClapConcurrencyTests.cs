using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Claps;
using Blog.Posts.Infrastructure.Persistence;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

[Collection(InfrastructureCollection.Name)]
public class ClapConcurrencyTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ContainerApiFactory _factory = null!;

    public ClapConcurrencyTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _factory = new ContainerApiFactory(_infrastructure);
        _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TwoHundredRacingRequestsFromOneReader_StopAtExactlyTheCap()
    {
        var postId = await PublishedPostAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => ClapAsync(postId, "racer", 1 + i % 10)));

        Assert.Equal(ClapRules.MaxPerReader, outcomes.Sum(o => o!.Applied));
        Assert.Equal(1, outcomes.Count(o => o!.FirstClap));

        await using var context = _factory.CreateContext();

        Assert.Equal(ClapRules.MaxPerReader, await context.PostClaps
            .Where(c => c.PostId == postId && c.UserId == "racer")
            .Select(c => c.Claps)
            .SingleAsync());
    }

    [Fact]
    public async Task ManyReadersAndThreeRollupWorkers_FoldEveryClapExactlyOnce()
    {
        var postId = await PublishedPostAsync();
        var rollup = _factory.Services.GetRequiredService<ClapRollupWorker>();

        using var clapping = new CancellationTokenSource();

        var workers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!clapping.IsCancellationRequested)
            {
                await rollup.FoldBatchAsync();
            }
        })).ToList();

        var claps = Enumerable.Range(0, 60).SelectMany(reader =>
            Enumerable.Range(0, 4).Select(_ => ClapAsync(postId, $"reader-{reader}", 20)));

        await Task.WhenAll(claps);

        clapping.Cancel();
        await Task.WhenAll(workers);

        while (await rollup.FoldBatchAsync() > 0)
        {
        }

        await using var context = _factory.CreateContext();

        var total = await context.PostClapTotals.SingleAsync(t => t.PostId == postId);

        Assert.Equal(60 * 50, total.Claps);
        Assert.Equal(60, total.Clappers);
        Assert.False(await context.ClapDeltas.AnyAsync(d => d.PostId == postId));

        var audit = await AuditAsync(postId);

        Assert.Empty(audit.Mismatches);
    }

    [Fact]
    public async Task ClapsQueuedForAPostThatIsThenDeleted_AreDropped_NotStuckOrFolded()
    {
        var postId = await PublishedPostAsync();
        var rollup = _factory.Services.GetRequiredService<ClapRollupWorker>();

        await ClapAsync(postId, "reader", 10);

        await using (var context = _factory.CreateContext())
        {
            await context.BlogPosts.Where(p => p.Id == postId).ExecuteDeleteAsync();
        }

        while (await rollup.FoldBatchAsync() > 0)
        {
        }

        await using var verify = _factory.CreateContext();

        Assert.False(await verify.ClapDeltas.AnyAsync(d => d.PostId == postId));
        Assert.False(await verify.PostClapTotals.AnyAsync(t => t.PostId == postId));
    }

    [Fact]
    public async Task TheAudit_StaysExact_WhileClapsAndRollupsRunUnderIt()
    {
        var postId = await PublishedPostAsync();
        var rollup = _factory.Services.GetRequiredService<ClapRollupWorker>();

        using var running = new CancellationTokenSource();

        var load = Task.Run(async () =>
        {
            var reader = 0;

            while (!running.IsCancellationRequested)
            {
                await ClapAsync(postId, $"auditee-{reader++}", 5);
                await rollup.FoldBatchAsync();
            }
        });

        for (var i = 0; i < 25; i++)
        {
            Assert.Empty((await AuditAsync(postId)).Mismatches);
        }

        running.Cancel();
        await load;
    }

    private async Task<int> PublishedPostAsync()
    {
        await using var context = _factory.CreateContext();

        var post = BlogPost.Publish($"claps-{Guid.NewGuid():N}", "Body", null, null, DateTime.UtcNow);

        context.BlogPosts.Add(post);
        await context.SaveChangesAsync();

        return post.Id;
    }

    private async Task<ClapOutcome?> ClapAsync(int postId, string userId, int count)
    {
        using var scope = _factory.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IClapStore>().ClapAsync(postId, userId, count);
    }

    private async Task<ClapAudit> AuditAsync(int postId)
    {
        using var scope = _factory.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IClapStore>().AuditAsync(postId);
    }
}
