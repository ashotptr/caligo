using System.Net;
using System.Net.Http.Json;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Claps;
using Blog.Posts.Application.Posts;
using Xunit;

namespace Blog.Api.Tests;

public class ClapApiTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public ClapApiTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AReader_IsCappedAtFiftyClapsPerPost()
    {
        var post = await PublishedPostAsync();
        var (reader, _) = await Authenticated.ClientAsync(_factory, "Reader");

        var first = await ClapAsync(reader, post.Id, 30);
        var second = await ClapAsync(reader, post.Id, 30);
        var third = await ClapAsync(reader, post.Id, 30);

        Assert.Equal((30, 30), (first.Applied, first.Mine));
        Assert.Equal((20, 50), (second.Applied, second.Mine));
        Assert.Equal((0, 50), (third.Applied, third.Mine));

        var tally = await reader.GetFromJsonAsync<ClapTally>($"/api/BlogPosts/{post.Id}/claps");

        Assert.Equal(50, tally!.Total);
        Assert.Equal(1, tally.Clappers);
        Assert.Equal(50, tally.Mine);
    }

    [Fact]
    public async Task ClapsFromSeveralReaders_AddUp()
    {
        var post = await PublishedPostAsync();

        for (var i = 0; i < 3; i++)
        {
            var (reader, _) = await Authenticated.ClientAsync(_factory, "Reader");

            await ClapAsync(reader, post.Id, 10);
        }

        var tally = await _factory.CreateClient().GetFromJsonAsync<ClapTally>($"/api/BlogPosts/{post.Id}/claps");

        Assert.Equal(30, tally!.Total);
        Assert.Equal(3, tally.Clappers);
        Assert.Equal(0, tally.Mine);
    }

    [Fact]
    public async Task AnAuthor_CannotClapForTheirOwnPost()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");
        var post = await CreateAsync(writer);

        var response = await writer.PostAsJsonAsync($"/api/BlogPosts/{post.Id}/claps", new { Count = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Clapping_RequiresAToken()
    {
        var post = await PublishedPostAsync();

        var response = await _factory.CreateClient()
            .PostAsJsonAsync($"/api/BlogPosts/{post.Id}/claps", new { Count = 5 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheAudit_IsAdminOnly_AndFindsNothingWrong()
    {
        var post = await PublishedPostAsync();
        var (reader, _) = await Authenticated.ClientAsync(_factory, "Reader");
        var (admin, _) = await Authenticated.ClientAsync(_factory, "Admin");

        await ClapAsync(reader, post.Id, 7);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/BlogPosts/claps/audit")).StatusCode);

        var audit = await admin.GetFromJsonAsync<ClapAudit>($"/api/BlogPosts/claps/audit?postId={post.Id}");

        Assert.Equal(1, audit!.PostsChecked);
        Assert.Equal(7, audit.ExpectedTotal);
        Assert.Empty(audit.Mismatches);
    }

    private async Task<PostDetailDto> PublishedPostAsync()
    {
        var (writer, _) = await Authenticated.ClientAsync(_factory, "Writer");

        return await CreateAsync(writer);
    }

    private static async Task<PostDetailDto> CreateAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/BlogPosts", new { Title = $"clappable-{Guid.NewGuid():N}", Content = "body" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PostDetailDto>())!;
    }

    private static async Task<ClapResult> ClapAsync(HttpClient reader, int postId, int count)
    {
        var response = await reader.PostAsJsonAsync($"/api/BlogPosts/{postId}/claps", new { Count = count });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ClapResult>())!;
    }
}
