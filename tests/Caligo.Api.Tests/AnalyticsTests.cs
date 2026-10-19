using System.Net;
using System.Net.Http.Json;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Domain.Posts;
using Caligo.Posts.Infrastructure.Analytics;
using Xunit;

namespace Caligo.Api.Tests;

public class AnalyticsTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public AnalyticsTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void NullPublisher_SwallowsEverything()
    {
        var publisher = new NullAnalyticsPublisher();

        publisher.PublishPostViewed(new PostViewedEvent { PostId = 1 });
    }

    [Fact]
    public async Task ViewingAPost_StillWorks_WithNoAnalyticsConfigured()
    {
        await using var context = _factory.CreateContext();

        var post = BlogPost.Publish("Read me", "body", null, null, DateTime.UtcNow);

        context.BlogPosts.Add(post);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/BlogPosts/{post.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostView_ForAMissingPost_Is404_AndNotCounted()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/BlogPosts/987654");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnalyticsEndpoint_ReportsDisabled_RatherThanFailing()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Analytics/posts/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("enabled"));
    }

    [Fact]
    public async Task TopPosts_RequiresAuthentication()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Analytics/top");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401 or 403, got {(int)response.StatusCode}");
    }
}
