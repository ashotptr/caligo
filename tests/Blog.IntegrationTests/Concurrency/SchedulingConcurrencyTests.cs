using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;
using Blog.Posts.Infrastructure.Persistence;
using Blog.Posts.Infrastructure.Publishing;
using Blog.SharedKernel;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

[Collection(InfrastructureCollection.Name)]
public class SchedulingConcurrencyTests : IAsyncLifetime
{
    private const int Quota = 5;

    private readonly InfrastructureFixture _infrastructure;
    private ContainerApiFactory _factory = null!;

    public SchedulingConcurrencyTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _factory = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["Posts:MaxScheduledPerAuthor"] = Quota.ToString()
        });

        _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ThreePublishersDrainingOneBurst_PublishEachPostExactlyOnce()
    {
        var marker = $"burst-{Guid.NewGuid():N}";

        await using (var context = _factory.CreateContext())
        {
            var due = DateTime.UtcNow.AddSeconds(-1);

            context.BlogPosts.AddRange(Enumerable.Range(0, 300)
                .Select(i => BlogPost.Schedule($"{marker}-{i}", "Body", null, null, due)));

            await context.SaveChangesAsync();
        }

        var publisher = _factory.Services.GetRequiredService<ScheduledPostPublisher>();

        var claimed = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var mine = 0;

            while (await publisher.PublishDueAsync() is var batch and > 0)
            {
                mine += batch;
            }

            return mine;
        })));

        await using var verify = _factory.CreateContext();

        var posts = await verify.BlogPosts
            .Where(p => p.Title.StartsWith(marker))
            .Select(p => new { p.Id, p.Status })
            .ToListAsync();

        Assert.All(posts, p => Assert.Equal(PostStatus.Published, p.Status));

        var keys = posts.Select(p => p.Id.ToString()).ToList();

        var announcements = await verify.OutboxMessages
            .Where(m => keys.Contains(m.PartitionKey) && m.EventType == "post.published")
            .GroupBy(m => m.PartitionKey)
            .Select(g => g.Count())
            .ToListAsync();

        Assert.Equal(300, announcements.Count);
        Assert.All(announcements, count => Assert.Equal(1, count));

        Assert.True(claimed.Sum() <= 300);
    }

    [Fact]
    public async Task TwentyRacingScheduleRequests_NeverPushAnAuthorPastTheQuota()
    {
        var authorId = await CreateAuthorAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            using var scope = _factory.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<PostService>().CreateAsync(
                new WritePostCommand($"queued-{i}", "Body", null, DateTime.UtcNow.AddDays(1)),
                new Caller(authorId, false));
        })));

        await using var context = _factory.CreateContext();

        var scheduled = await context.BlogPosts.CountAsync(
            p => p.AuthorId == authorId && p.Status == PostStatus.Scheduled);

        Assert.Equal(Quota, scheduled);
        Assert.Equal(Quota, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(ResultStatus.Conflict, r.Status));
    }

    private async Task<string> CreateAuthorAsync()
    {
        using var scope = _factory.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var author = new ApplicationUser
        {
            UserName = $"author-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test"
        };

        var created = await users.CreateAsync(author, "Str0ng!Passw0rd");

        Assert.True(created.Succeeded);

        return author.Id;
    }
}
