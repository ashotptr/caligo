using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClickHouse.Client.ADO;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Blog.Analytics.Infrastructure.Stores;
using Blog.Api.Services;
using Blog.Contracts.Analytics;
using Blog.Posts.Infrastructure.Identity;
using Xunit;

namespace Blog.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class LabResultsTests : IAsyncLifetime
{
    private const double MiB = 1024 * 1024;

    private readonly InfrastructureFixture _infrastructure;
    private ClickHouseLabStore _store = null!;

    public LabResultsTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        var options = Options.Create(new ClickHouseOptions { ConnectionString = _infrastructure.ClickHouseConnectionString });

        await new ClickHouseOlapStore(options, NullLogger<ClickHouseOlapStore>.Instance).EnsureSchemaAsync();

        _store = new ClickHouseLabStore(options);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Runs_AreListedOnceWithTheirLatestFinish_NewestFirst()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var start = Second(DateTime.UtcNow.AddHours(-3));

        await RunAsync(older, start, start);
        await RunAsync(older, start, start.AddMinutes(5));
        await RunAsync(newer, start.AddHours(1), start.AddHours(1).AddMinutes(3));

        var runs = (await _store.RunsAsync(500)).Where(run => run.RunId == older || run.RunId == newer).ToList();

        Assert.Equal(new[] { newer, older }, runs.Select(run => run.RunId));
        Assert.Equal(start.AddMinutes(5), runs[1].FinishedAt);
        Assert.Equal(DateTimeKind.Utc, runs[1].StartedAt.Kind);
        Assert.Equal("baseline", runs[1].Scenario);
        Assert.Equal(27.5, runs[1].SourceBiasMs);
        Assert.Null(await _store.RunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ARun_IsSummarisedPerContestant()
    {
        var run = Guid.NewGuid();
        var start = Second(DateTime.UtcNow.AddHours(-1));

        await RunAsync(run, start, start.AddMinutes(3));

        await ExecuteAsync("INSERT INTO lab_windows VALUES " + string.Join(", ",
            Window(run, "relay-mp3", "relay", "http-mp3", "blog-relay-1", 1, start, 30, 100, 2, 1),
            Window(run, "relay-mp3", "relay", "http-mp3", "blog-relay-1", 2, start.AddSeconds(60), 30, 50, 1, 0),
            Window(run, "icecast", "icecast", "http-mp3", "blog-icecast-1", 1, start.AddSeconds(30), 30, 0, 0, 0),
            Window(run, "icecast", "icecast", "http-mp3", "blog-icecast-1", 2, start.AddSeconds(90), 30, 0, 0, 0),
            Window(run, "mediamtx-webrtc", "mediamtx", "webrtc", "blog-mediamtx-1", 1, start.AddSeconds(120), 12, 0, 0, 0)));

        await ExecuteAsync("INSERT INTO lab_results VALUES " + string.Join(", ",
            Listener(run, "relay-mp3", 1, 0, start, 10, 100, 0, 0, 1000, 120),
            Listener(run, "relay-mp3", 1, 1, start, 20, 150, 1, 250, 1050, 130),
            Listener(run, "relay-mp3", 1, 2, start, 30, 200, 0, 0, 1100, 140),
            Listener(run, "relay-mp3", 2, 0, start, 10, 200, 0, 0, 1100, 120),
            Listener(run, "relay-mp3", 2, 1, start, 20, 250, 0, 0, 1150, 130),
            Listener(run, "relay-mp3", 2, 2, start, 30, 900, 2, 400, 1400, 140),
            Listener(run, "icecast", 1, 0, start, 50, 700, 0, 0, 2000, 900),
            Listener(run, "icecast", 1, 1, start, null, null, 0, 0, null, null, "connection refused"),
            Listener(run, "icecast", 1, 2, start, null, null, 0, 0, null, null, "timed out"),
            Listener(run, "icecast", 2, 0, start, 60, 800, 0, 0, 2100, 950),
            Listener(run, "icecast", 2, 1, start, 70, 800, 0, 0, 2100, 1000),
            Listener(run, "mediamtx-webrtc", 1, 0, start, null, null, 0, 0, null, null, "not connected within the 12 s window"),
            Listener(run, "mediamtx-webrtc", 1, 1, start, null, null, 0, 0, null, null, "not connected within the 12 s window")));

        await ExecuteAsync("INSERT INTO lab_resources VALUES " + string.Join(", ",
            Sample(run, "blog-relay-1", start.AddSeconds(10), 10, 100),
            Sample(run, "blog-relay-1", start.AddSeconds(20), 30, 120),
            Sample(run, "blog-relay-1", start.AddSeconds(45), 99, 999),
            Sample(run, "blog-relay-1", start.AddSeconds(70), 20, 110)));

        await ExecuteAsync("INSERT INTO lab_latency VALUES " + string.Join(", ",
            Latency(run, "relay-mp3", 1, start.AddMilliseconds(1100), 1000),
            Latency(run, "relay-mp3", 1, start.AddMilliseconds(1500), 1200),
            Latency(run, "relay-mp3", 1, start.AddMilliseconds(1900), 1100),
            Latency(run, "relay-mp3", 1, start.AddMilliseconds(2500), 1300),
            Latency(run, "icecast", 1, start.AddMilliseconds(31500), 2000)));

        var detail = await _store.RunAsync(run);

        Assert.NotNull(detail);
        Assert.Equal(run, detail!.Run.RunId);
        Assert.Equal(new[] { "icecast", "mediamtx-webrtc", "relay-mp3" }, detail.Contestants.Select(c => c.Contestant));

        var relay = detail.Contestants[2];

        Assert.Equal(("relay", "http-mp3", "blog-relay-1"), (relay.Server, relay.Protocol, relay.Container));
        Assert.Equal((2, 6L, 6L), (relay.Repetitions, relay.Listeners, relay.Connected));
        Assert.Equal(20, relay.TtfbP50Ms);
        Assert.Equal(200, relay.StartupP50Ms);
        Assert.Equal(900, relay.StartupP95Ms);
        Assert.Equal(100, relay.StartupSpreadMs);
        Assert.Equal((3L, 650.0, 180000.0), (relay.Stalls, relay.StallMs, relay.MediaMs));
        Assert.Equal(1100, relay.LatencyP50Ms);
        Assert.Equal(100, relay.LatencySpreadMs);
        Assert.Equal(130, relay.DataLatencyP50Ms);
        Assert.Equal(20, relay.CpuMeanPercent!.Value, 3);
        Assert.Equal(30, relay.CpuPeakPercent);
        Assert.Equal(120 * MiB, relay.MemoryPeakBytes);
        Assert.Equal((150L, 3L, 1L), (relay.ImpairedPackets, relay.ImpairedLost, relay.ImpairedOverflowed));
        Assert.Equal(string.Empty, relay.Errors);

        var icecast = detail.Contestants[0];

        Assert.Equal((2, 5L, 3L), (icecast.Repetitions, icecast.Listeners, icecast.Connected));
        Assert.Equal(60, icecast.TtfbP50Ms);
        Assert.Equal(800, icecast.StartupP50Ms);
        Assert.Equal(100, icecast.StartupSpreadMs);
        Assert.Equal(2100, icecast.LatencyP50Ms);
        Assert.Equal(100, icecast.LatencySpreadMs);
        Assert.Equal(950, icecast.DataLatencyP50Ms);
        Assert.Null(icecast.CpuMeanPercent);
        Assert.Null(icecast.MemoryPeakBytes);
        Assert.Contains("connection refused", icecast.Errors);
        Assert.Contains("timed out", icecast.Errors);

        var webrtc = detail.Contestants[1];

        Assert.Equal((1, 2L, 0L), (webrtc.Repetitions, webrtc.Listeners, webrtc.Connected));
        Assert.Null(webrtc.StartupP50Ms);
        Assert.Null(webrtc.StartupSpreadMs);
        Assert.Null(webrtc.LatencyP50Ms);
        Assert.Equal(0, webrtc.MediaMs);
        Assert.Equal("not connected within the 12 s window", webrtc.Errors);

        Assert.Equal(
            new[]
            {
                ("relay-mp3", 1, start.AddSeconds(1), 1100.0),
                ("relay-mp3", 1, start.AddSeconds(2), 1300.0),
                ("icecast", 1, start.AddSeconds(31), 2000.0)
            },
            detail.Latency.Select(point => (point.Contestant, point.Repetition, point.MeasuredAt, point.LatencyMs)));
    }

    [Fact]
    public async Task TheBlogApi_ServesARunToAdministrators_FromTheAnalyticsService()
    {
        var run = Guid.NewGuid();
        var start = Second(DateTime.UtcNow.AddMinutes(-30));

        await RunAsync(run, start, start.AddMinutes(1));
        await ExecuteAsync("INSERT INTO lab_windows VALUES " +
            Window(run, "relay-objects", "relay", "objects-ws", "blog-relay-1", 1, start, 12, 0, 0, 0));
        await ExecuteAsync("INSERT INTO lab_results VALUES " +
            Listener(run, "relay-objects", 1, 0, start, 5, 17, 1, 200, 751, 173));
        await ExecuteAsync("INSERT INTO lab_latency VALUES " +
            Latency(run, "relay-objects", 1, start.AddSeconds(3), 751));

        using var analytics = new AnalyticsHostFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClickHouse"] = _infrastructure.ClickHouseConnectionString
        });

        var analyticsServer = analytics.Server;

        using var blog = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["Analytics:Address"] = analyticsServer.BaseAddress.ToString(),
            ["Analytics:OlapDeadline"] = "00:00:20"
        });

        using var wired = blog.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services
                .AddGrpcClient<AnalyticsReader.AnalyticsReaderClient>(options => options.Address = analyticsServer.BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => analyticsServer.CreateHandler())));

        var admin = await SignedInAsync(wired, "Admin");
        var runs = await admin.GetFromJsonAsync<JsonElement>("/api/lab/runs");

        Assert.True(runs.GetProperty("enabled").GetBoolean());
        Assert.Contains(runs.GetProperty("runs").EnumerateArray(), listed => listed.GetProperty("runId").GetGuid() == run);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/lab/runs/{run}");
        var contestant = detail.GetProperty("contestants").EnumerateArray().Single();

        Assert.True(detail.GetProperty("enabled").GetBoolean());
        Assert.Equal(run, detail.GetProperty("run").GetProperty("runId").GetGuid());
        Assert.Equal("relay-objects", contestant.GetProperty("contestant").GetString());
        Assert.Equal("objects-ws", contestant.GetProperty("protocol").GetString());
        Assert.Equal(17, contestant.GetProperty("startupP50Ms").GetDouble());
        Assert.Equal(2, contestant.GetProperty("stallsPerListenerMinute").GetDouble());
        Assert.Equal(751, contestant.GetProperty("latencyP50Ms").GetDouble());
        Assert.Equal(JsonValueKind.Null, contestant.GetProperty("cpuMeanPercent").ValueKind);
        Assert.Single(detail.GetProperty("latency").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/lab/runs/{Guid.NewGuid()}")).StatusCode);

        var reader = await SignedInAsync(wired);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/lab/runs/{run}")).StatusCode);
    }

    private Task RunAsync(Guid run, DateTime startedAt, DateTime finishedAt) => ExecuteAsync(
        $"INSERT INTO lab_runs VALUES ('{run}', 'baseline', 'delay_ms=0 scope=flow', {Time(startedAt)}, {Time(finishedAt)}, " +
        "3, 30, 2, 3, 27.5, 'written by a test')");

    private static string Window(
        Guid run, string contestant, string server, string protocol, string container, int repetition, DateTime startedAt, int seconds,
        long packets, long lost, long overflowed) =>
        $"('{run}', '{contestant}', '{server}', '{protocol}', '{container}', {repetition}, {Time(startedAt)}, " +
        $"{Time(startedAt.AddSeconds(seconds))}, {packets}, {lost}, {overflowed})";

    private static string Listener(
        Guid run, string contestant, int repetition, int listener, DateTime at, double? ttfbMs, double? startupMs, int stalls,
        double stallMs, double? latencyMs, double? dataLatencyMs, string error = "") =>
        $"('{run}', '{contestant}', {repetition}, {listener}, {(listener == 0 ? "true" : "false")}, {Time(at)}, " +
        $"{(startupMs is null ? "false" : "true")}, {Number(ttfbMs)}, {Number(startupMs)}, {stalls}, {Number(stallMs)}, " +
        $"{(startupMs is null ? "0" : "30000")}, 480000, {Number(latencyMs)}, {Number(dataLatencyMs)}, '{error}')";

    private static string Sample(Guid run, string container, DateTime at, double cpuPercent, int memoryMiB) =>
        $"('{run}', '{container}', {Time(at)}, {Number(cpuPercent)}, {(long)(memoryMiB * MiB)})";

    private static string Latency(Guid run, string contestant, int repetition, DateTime at, double latencyMs) =>
        $"('{run}', '{contestant}', {repetition}, {Time(at)}, {Number(latencyMs)}, NULL)";

    private static string Time(DateTime value) =>
        "'" + value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";

    private static string Number(double? value) =>
        value is null ? "NULL" : value.Value.ToString(CultureInfo.InvariantCulture);

    private static DateTime Second(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private async Task ExecuteAsync(string sql)
    {
        using var connection = new ClickHouseConnection(_infrastructure.ClickHouseConnectionString);
        using var command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"lab-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test"
        };

        var created = await users.CreateAsync(user, "Str0ng!Passw0rd");

        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, roles);
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }
}
