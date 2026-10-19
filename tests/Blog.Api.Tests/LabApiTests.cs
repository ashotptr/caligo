using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Blog.Api.Lab;
using Xunit;

namespace Blog.Api.Tests;

public class LabApiTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public LabApiTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LabRuns_AreForAdministratorsOnly()
    {
        var anonymous = await _factory.CreateClient().GetAsync("/api/lab/runs");
        var (reader, _) = await Authenticated.ClientAsync(_factory);

        Assert.True(
            anonymous.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401 or 403, got {(int)anonymous.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/lab/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/lab/runs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task WithoutTheAnalyticsService_TheListSaysSo_AndNoRunIsFound()
    {
        var (admin, _) = await Authenticated.ClientAsync(_factory, "Admin");

        var runs = await admin.GetFromJsonAsync<JsonElement>("/api/lab/runs");

        Assert.False(runs.GetProperty("enabled").GetBoolean());
        Assert.Empty(runs.GetProperty("runs").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/lab/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/lab/runs/not-a-run")).StatusCode);
    }

    [Fact]
    public async Task WhenTheAnalyticsServiceCannotBeReached_ItIsReportedAsUnavailable_NotAsMissing()
    {
        using var unreachable = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:Address"] = "http://127.0.0.1:1"
            })));
        var (admin, _) = await Authenticated.ClientAsync(unreachable, "Admin");

        var runs = await admin.GetAsync("/api/lab/runs");
        var run = await admin.GetAsync($"/api/lab/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, runs.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, run.StatusCode);
        Assert.Equal(LabResults.UnavailableMessage, await run.Content.ReadAsStringAsync());
    }
}
