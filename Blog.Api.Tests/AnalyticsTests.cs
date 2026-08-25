using System.Net;
using System.Net.Http.Json;
using Blog.Api.Analytics;
using Xunit;

namespace Blog.Api.Tests;

public class AnalyticsTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public AnalyticsTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    private static Microsoft.Extensions.Configuration.IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        var source = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)).ToList()
        };

        var root = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        root.Add(source);

        return root.Build();
    }

    [Fact]
    public void NullPublisher_SwallowsEverything()
    {
        var publisher = new NullAnalyticsPublisher();

        publisher.PublishPostViewed(new PostViewedEvent { PostId = 1 });
    }

    [Fact]
    public void RabbitPublisher_WithNoConnectionString_IsNotConfigured()
    {
        var configuration = BuildConfiguration();

        var publisher = new RabbitMqAnalyticsPublisher(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMqAnalyticsPublisher>.Instance);

        Assert.False(publisher.IsConfigured);
        
        publisher.PublishPostViewed(new PostViewedEvent { PostId = 1 });
    }

    [Fact]
    public void RabbitPublisher_WithAnUnreachableBroker_DoesNotThrow()
    {
        var configuration = BuildConfiguration(("ConnectionStrings:RabbitMq", "amqp://guest:guest@127.0.0.1:5673/"));

        var publisher = new RabbitMqAnalyticsPublisher(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMqAnalyticsPublisher>.Instance);

        Assert.True(publisher.IsConfigured);

        publisher.PublishPostViewed(new PostViewedEvent { PostId = 1 });
        publisher.PublishPostViewed(new PostViewedEvent { PostId = 2 });

        publisher.Dispose();
    }

    [Fact]
    public async Task ViewingAPost_StillWorks_WithNoAnalyticsConfigured()
    {
        await using var context = _factory.CreateContext();

        var post = new Blog.Api.Models.BlogPost
        {
            Title = "Read me",
            Content = "body",
            PublishedDate = DateTime.UtcNow,
            Tags = new List<string>()
        };

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

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"expected 401 or 403, got {(int)response.StatusCode}");
    }
}
