using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Blog.Contracts;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Domain.Events;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Persistence;
using Xunit;

namespace Blog.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class OutboxTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ContainerApiFactory _factory = null!;

    public OutboxTests(InfrastructureFixture infrastructure)
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
    public async Task PublishingAPost_WritesAnOutboxRow()
    {
        var title = $"outbox-{Guid.NewGuid():N}";

        using var scope = _factory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IPostRepository>();
        var post = BlogPost.Publish(title, "Body", new[] { "kafka" }, null, DateTime.UtcNow);

        repository.Add(post);
        await repository.SaveChangesAsync();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var message = await context.OutboxMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.PartitionKey == post.Id.ToString());

        Assert.NotNull(message);
        Assert.Equal("post.published", message!.EventType);
        Assert.Equal(Topics.PostLifecycle, message.Topic);

        Assert.Contains($"\"postId\": {post.Id}", message.Payload.Replace(" ", " "));
    }

    [Fact]
    public async Task WhenTheOutboxWriteFails_ThePostIsNotSavedEither()
    {
        var title = $"rollback-{Guid.NewGuid():N}";

        using var scope = _factory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var exploding = new Mock<IDomainEventDispatcher>();
        exploding
            .Setup(d => d.DispatchAsync(It.IsAny<IReadOnlyList<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("outbox unavailable"));

        var repository = new EfPostRepository(context, exploding.Object);

        repository.Add(BlogPost.Publish(title, "Body", null, null, DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveChangesAsync());

        using var verifyScope = _factory.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.False(
            await verify.BlogPosts.AsNoTracking().AnyAsync(p => p.Title == title),
            "the post was committed even though its outbox row was not");
    }

    [Fact]
    public async Task TheRelay_PublishesOutboxRowsToKafka_AndMarksThemSent()
    {
        var title = $"relayed-{Guid.NewGuid():N}";

        using var scope = _factory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IPostRepository>();
        var post = BlogPost.Publish(title, "Body", new[] { "relay" }, null, DateTime.UtcNow);

        repository.Add(post);
        await repository.SaveChangesAsync();

        var payload = await ReadFromKafkaAsync(
            Topics.PostLifecycle,
            $"test-{Guid.NewGuid():N}",
            json => json.Contains(title, StringComparison.Ordinal),
            TimeSpan.FromSeconds(45));

        Assert.NotNull(payload);
        Assert.Contains(title, payload);

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sent = await WaitForAsync(
            async () => await context.OutboxMessages
                .AsNoTracking()
                .AnyAsync(m => m.PartitionKey == post.Id.ToString() && m.ProcessedAt != null),
            TimeSpan.FromSeconds(20));

        Assert.True(sent, "the outbox row was published but never marked as processed");
    }

    [Fact]
    public async Task TheEventIsKeyedByPostId_SoOnePostsEventsShareAPartition()
    {
        using var scope = _factory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IPostRepository>();
        var post = BlogPost.Publish($"keyed-{Guid.NewGuid():N}", "Body", null, null, DateTime.UtcNow);

        repository.Add(post);
        await repository.SaveChangesAsync();

        var tracked = await repository.FindAsync(post.Id);
        tracked!.Revise("Revised", "Body", null);
        await repository.SaveChangesAsync();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var keys = await context.OutboxMessages
            .AsNoTracking()
            .Where(m => m.PartitionKey == post.Id.ToString())
            .Select(m => m.EventType)
            .ToListAsync();

        Assert.Contains("post.published", keys);
        Assert.Contains("post.updated", keys);
    }

    private async Task<string?> ReadFromKafkaAsync(
        string topic, string group, Func<string, bool> matches, TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _infrastructure.KafkaBootstrapServers,
            GroupId = group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();

        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));

            if (result?.Message?.Value is null)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(result.Message.Value);

            if (matches(json))
            {
                consumer.Close();

                return json;
            }
        }

        consumer.Close();

        return null;
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

            await Task.Delay(250);
        }

        return false;
    }
}
