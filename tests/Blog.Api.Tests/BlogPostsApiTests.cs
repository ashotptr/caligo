using System.Net;
using System.Net.Http.Json;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;
using Blog.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blog.Api.Tests;

public class BlogPostsApiTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public BlogPostsApiTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetBlogPosts_ReturnsAnEmptyPage_OnAFreshDatabase()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/BlogPosts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        Assert.NotNull(page);
        Assert.NotNull(page!.Items);
    }

    [Fact]
    public async Task GetBlogPost_ReturnsNotFound_ForAnIdThatDoesNotExist()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/BlogPosts/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreatingAPost_RequiresAuthentication()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/BlogPosts",
            new { Title = "No token", Content = "should be rejected", Tags = new[] { "x" } });

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401 or 403, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task AnUnknownRoute_Returns404_RatherThanCrashing()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/NoSuchThing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tags_SurviveARoundTripThroughSqlite()
    {
        await using var context = _factory.CreateContext();

        var post = BlogPost.Publish(
            "Tagged", "body", new[] { "csharp", "sqlite", "ef-core" }, null, DateTime.UtcNow);

        context.BlogPosts.Add(post);
        await context.SaveChangesAsync();

        await using var reader = _factory.CreateContext();
        var stored = await reader.BlogPosts.AsNoTracking()
            .FirstAsync(p => p.Id == post.Id);

        Assert.Equal(3, stored.Tags.Count);
        Assert.Contains("sqlite", stored.Tags);
        Assert.Equal(new[] { "csharp", "sqlite", "ef-core" }, stored.Tags);
    }

    [Fact]
    public async Task RevisingAPost_PersistsTheNewTags()
    {
        await using var context = _factory.CreateContext();

        var post = BlogPost.Publish("Mutating tags", "body", new[] { "first" }, null, DateTime.UtcNow);

        context.BlogPosts.Add(post);
        await context.SaveChangesAsync();

        var tracked = await context.BlogPosts.FirstAsync(p => p.Id == post.Id);
        tracked.Revise("Mutating tags", "body", new[] { "first", "second" });
        await context.SaveChangesAsync();

        await using var reader = _factory.CreateContext();
        var stored = await reader.BlogPosts.AsNoTracking().FirstAsync(p => p.Id == post.Id);

        Assert.Equal(2, stored.Tags.Count);
        Assert.Contains("second", stored.Tags);
    }

    [Fact]
    public async Task APostWrittenDirectly_IsVisibleThroughTheApi()
    {
        var post = await SeedPostAsync("Visible through HTTP");

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/BlogPosts/{post.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<PostDetailDto>();

        Assert.NotNull(detail);
        Assert.Equal("Visible through HTTP", detail!.Title);
    }

    [Fact]
    public async Task Paging_WalksEveryPost_ExactlyOnce()
    {
        var titles = Enumerable.Range(0, 7).Select(i => $"paged-{Guid.NewGuid():N}-{i}").ToList();

        var sharedTimestamp = DateTime.UtcNow;

        foreach (var title in titles)
        {
            await SeedPostAsync(title, sharedTimestamp);
        }

        var client = _factory.CreateClient();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var url = cursor is null
                ? "/api/BlogPosts?limit=2"
                : $"/api/BlogPosts?limit=2&cursor={Uri.EscapeDataString(cursor)}";

            var page = await client.GetFromJsonAsync<PagedResult<PostSummaryDto>>(url);

            Assert.NotNull(page);

            seen.AddRange(page!.Items.Select(p => p.Title));
            cursor = page.NextCursor;

            Assert.True(++pages < 50, "paging did not terminate");
        }
        while (cursor is not null);

        foreach (var title in titles)
        {
            Assert.Equal(1, seen.Count(t => t == title));
        }
    }

    [Fact]
    public async Task Paging_ClampsAnAbsurdLimit_RatherThanHonouringIt()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedPostAsync($"clamp-{Guid.NewGuid():N}");
        }

        var client = _factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/BlogPosts?limit=100000");

        Assert.NotNull(page);
        Assert.True(page!.Items.Count <= 50, $"limit was not clamped: got {page.Items.Count} items");
    }

    [Fact]
    public async Task Paging_TreatsAGarbageCursor_AsTheFirstPage()
    {
        await SeedPostAsync($"garbage-cursor-{Guid.NewGuid():N}");

        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/BlogPosts?cursor=not-a-real-cursor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResult<PostSummaryDto>>();

        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
    }

    [Fact]
    public async Task Search_FindsAPostByTitle()
    {
        var marker = $"zqx{Guid.NewGuid():N}";

        await SeedPostAsync($"Findable {marker}");

        var client = _factory.CreateClient();

        var results = await client.GetFromJsonAsync<List<PostSummaryDto>>(
            $"/api/BlogPosts/search?query={marker}");

        Assert.NotNull(results);
        Assert.Contains(results!, p => p.Title.Contains(marker));
    }

    [Fact]
    public async Task Search_RejectsAnEmptyQuery()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/BlogPosts/search?query=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<BlogPost> SeedPostAsync(string title, DateTime? publishedAt = null)
    {
        await using var context = _factory.CreateContext();

        var post = BlogPost.Publish(title, "body", null, null, publishedAt ?? DateTime.UtcNow);

        context.BlogPosts.Add(post);
        await context.SaveChangesAsync();

        return post;
    }
}
