using System.Net;
using System.Net.Http.Json;
using Blog.Posts.Application.Posts;
using Blog.Posts.Infrastructure.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blog.Api.Tests;

public class ConcurrencyContractTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public ConcurrencyContractTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ReadingAPost_ReturnsItsVersionAsAnETag()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var created = await CreateAsync(writer, "etag");

        var response = await writer.GetAsync($"/api/BlogPosts/{created.Id}");

        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task AnUpdateWithTheCurrentETag_Succeeds_AndMovesTheETagOn()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var created = await CreateAsync(writer, "fresh");

        var update = await SendAsync(writer, HttpMethod.Put, created.Id, "\"0\"", Body("fresh, edited"));

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var reread = await writer.GetAsync($"/api/BlogPosts/{created.Id}");

        Assert.Equal("\"1\"", reread.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task AnUpdateWithAStaleETag_Is412_AndTheNewerVersionSurvives()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var created = await CreateAsync(writer, "contested");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await SendAsync(writer, HttpMethod.Put, created.Id, "\"0\"", Body("first writer wins"))).StatusCode);

        var stale = await SendAsync(writer, HttpMethod.Put, created.Id, "\"0\"", Body("second writer, stale copy"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var current = await writer.GetFromJsonAsync<PostDetailDto>($"/api/BlogPosts/{created.Id}");

        Assert.Equal("first writer wins", current!.Title);
    }

    [Fact]
    public async Task ADeleteWithAStaleETag_Is412_AndThePostIsKept()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var created = await CreateAsync(writer, "keep me");

        await SendAsync(writer, HttpMethod.Put, created.Id, null, Body("keep me, edited"));

        var delete = await SendAsync(writer, HttpMethod.Delete, created.Id, "\"0\"", null);

        Assert.Equal(HttpStatusCode.PreconditionFailed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync($"/api/BlogPosts/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task AScheduledPost_IsInvisibleToEveryoneButItsAuthor_UntilItIsDue()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var anonymous = _factory.CreateClient();

        var title = $"scheduled-{Guid.NewGuid():N}";

        var response = await writer.PostAsJsonAsync("/api/BlogPosts", new
        {
            Title = title,
            Content = "Goes live later.",
            PublishAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = (await response.Content.ReadFromJsonAsync<PostDetailDto>())!;

        Assert.Equal(PostStatuses.Scheduled, created.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/BlogPosts/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync($"/api/BlogPosts/{created.Id}")).StatusCode);
        Assert.DoesNotContain(await PublicTitlesAsync(anonymous), t => t == title);

        var mine = await writer.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/BlogPosts?mine=true");
        Assert.Contains(mine!.Items, p => p.Title == title && p.Status == PostStatuses.Scheduled);

        await using (var context = _factory.CreateContext())
        {
            await context.BlogPosts
                .Where(p => p.Id == created.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ScheduledFor, DateTime.UtcNow.AddMinutes(-1)));
        }

        await _factory.Services.GetRequiredService<ScheduledPostPublisher>().PublishDueAsync();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/BlogPosts/{created.Id}")).StatusCode);
        Assert.Contains(await PublicTitlesAsync(anonymous), t => t == title);
    }

    [Fact]
    public async Task SchedulingPastTheQuota_IsAConflict()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");

        var rules = _factory.Services.GetRequiredService<PostingRules>();

        for (var i = 0; i < rules.MaxScheduledPerAuthor; i++)
        {
            var accepted = await writer.PostAsJsonAsync("/api/BlogPosts", new
            {
                Title = $"queued-{i}",
                Content = "Later.",
                PublishAt = DateTimeOffset.UtcNow.AddDays(1)
            });

            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        var overQuota = await writer.PostAsJsonAsync("/api/BlogPosts", new
        {
            Title = "one too many",
            Content = "Later.",
            PublishAt = DateTimeOffset.UtcNow.AddDays(1)
        });

        Assert.Equal(HttpStatusCode.Conflict, overQuota.StatusCode);
    }

    private static object Body(string title) => new { Title = title, Content = "body", Tags = Array.Empty<string>() };

    private static async Task<PostDetailDto> CreateAsync(HttpClient writer, string title)
    {
        var response = await writer.PostAsJsonAsync("/api/BlogPosts", Body($"{title}-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PostDetailDto>())!;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, int id, string? ifMatch, object? body)
    {
        var request = new HttpRequestMessage(method, $"/api/BlogPosts/{id}");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private static async Task<List<string>> PublicTitlesAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/BlogPosts?limit=50");

        return page!.Items.Select(p => p.Title).ToList();
    }
}
