using System.Net;
using Xunit;

namespace Caligo.Api.Tests;

public class CatalogDisabledTests : IClassFixture<SqliteApiFactory>
{
    private readonly HttpClient _client;

    public CatalogDisabledTests(SqliteApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/content")]
    [InlineData("/api/content/3")]
    [InlineData("/api/series")]
    [InlineData("/api/insights/popular")]
    [InlineData("/api/subscription/plans")]
    [InlineData("/api/songs/lookup?artist=a&title=b")]
    [InlineData("/api/content/3/media")]
    public async Task WithoutPostgres_TheCatalogSaysItIsSwitchedOff(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAnalytics_RadioHistorySaysItIsOff_RatherThanFailing()
    {
        var response = await _client.GetAsync("/api/radio/recent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("plays").GetArrayLength());
    }

    [Fact]
    public async Task WithoutPostgres_TheInternalMediaEndpointsAreSwitchedOff()
    {
        var claim = await _client.PostAsync("/internal/v1/packaging/claim", null);
        var session = await _client.GetAsync($"/internal/v1/media/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, claim.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, session.StatusCode);
    }

    [Fact]
    public async Task WithoutPostgres_PostsStillWork()
    {
        var response = await _client.GetAsync("/api/BlogPosts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
