using Caligo.Posts.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caligo.Api.Tests;

public class PostCacheTests
{
    private readonly SingleFlight _flights = new();
    private readonly LocalGenerationCounters _generations = new();

    private DistributedPostCache Cache() => new(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        _generations,
        _flights,
        NullLogger<DistributedPostCache>.Instance);

    [Fact]
    public async Task AHundredConcurrentMisses_RunTheLoaderOnce()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();
        var loads = 0;

        var readers = Enumerable.Range(0, 100)
            .Select(_ => cache.GetOrLoadAsync<Payload>(
                "hot",
                async _ =>
                {
                    Interlocked.Increment(ref loads);

                    await gate.Task;

                    return new Payload("from the database");
                },
                _ => true,
                TimeSpan.FromMinutes(5)))
            .ToList();

        await WaitUntilAsync(() => _flights.InFlight == 1);

        gate.SetResult();

        var results = await Task.WhenAll(readers);

        Assert.Equal(1, loads);
        Assert.All(results, r => Assert.Equal("from the database", r!.Value));
    }

    [Fact]
    public async Task AFollowerThatGivesUp_DoesNotCancelTheLoadForEveryoneElse()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();

        var leader = cache.GetOrLoadAsync<Payload>(
            "hot",
            async _ =>
            {
                await gate.Task;

                return new Payload("loaded");
            },
            _ => true,
            TimeSpan.FromMinutes(5));

        await WaitUntilAsync(() => _flights.InFlight == 1);

        using var impatient = new CancellationTokenSource();

        var follower = cache.GetOrLoadAsync<Payload>(
            "hot", _ => Task.FromResult<Payload?>(new Payload("never")), _ => true, TimeSpan.FromMinutes(5),
            impatient.Token);

        impatient.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);

        gate.SetResult();

        Assert.Equal("loaded", (await leader)!.Value);
    }

    [Fact]
    public async Task ALoadThatStartedBeforeAnInvalidation_IsNeverServedAfterIt()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();

        var generation = await cache.GetGenerationAsync("posts:1");

        var slowReader = cache.GetOrLoadAsync<Payload>(
            $"detail:{generation}",
            async _ =>
            {
                await gate.Task;

                return new Payload("stale");
            },
            _ => true,
            TimeSpan.FromMinutes(5));

        await cache.InvalidateAsync("posts:1");

        gate.SetResult();
        await slowReader;

        var next = await cache.GetGenerationAsync("posts:1");

        var fresh = await cache.GetOrLoadAsync<Payload>(
            $"detail:{next}", _ => Task.FromResult<Payload?>(new Payload("fresh")), _ => true, TimeSpan.FromMinutes(5));

        Assert.Equal("fresh", fresh!.Value);
    }

    [Fact]
    public async Task AValueTheCallerRefusesToCache_IsReturnedButNotStored()
    {
        var cache = Cache();
        var loads = 0;

        for (var i = 0; i < 2; i++)
        {
            await cache.GetOrLoadAsync<Payload>(
                "draft",
                _ =>
                {
                    loads++;

                    return Task.FromResult<Payload?>(new Payload("scheduled post"));
                },
                _ => false,
                TimeSpan.FromMinutes(5));
        }

        Assert.Equal(2, loads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met in time");

            await Task.Delay(10);
        }
    }

    public record Payload(string Value);
}
