using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Playback;
using Blog.Analytics.Infrastructure.Stores;
using Blog.Api.Services;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Domain.Media;
using Blog.Catalog.Infrastructure.Persistence;
using Blog.Contracts.Analytics;
using Blog.Posts.Infrastructure.Identity;
using Xunit;
using CoreSummary = Blog.Analytics.Core.Playback.PlaybackSummary;

namespace Blog.IntegrationTests;

[Collection(InfrastructureCollection.Name)]
public class PlaybackAnalyticsTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ClickHousePlaybackStore _store = null!;

    public PlaybackAnalyticsTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        var options = Options.Create(new ClickHouseOptions { ConnectionString = _infrastructure.ClickHouseConnectionString });

        await new ClickHouseOlapStore(options, NullLogger<ClickHouseOlapStore>.Instance).EnsureSchemaAsync();

        _store = new ClickHousePlaybackStore(options);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ResentEvents_CountOnce_AndEachTitleIsSummarised()
    {
        var content = Random.Shared.Next(1_000_000, 2_000_000);
        var hls = Guid.NewGuid();
        var dash = Guid.NewGuid();
        var progressive = Guid.NewGuid();
        var startup = Event(content, hls, "device-hls-0001", 1, "startup", "hls", 600);

        await _store.WriteAsync(new[]
        {
            startup,
            Event(content, hls, "device-hls-0001", 2, "stall", "hls", 1200),
            Event(content, hls, "device-hls-0001", 3, "quality", "hls", 0, 360, 720),
            Event(content, dash, "device-dash-001", 1, "startup", "dash", 900),
            Event(content, dash, "device-dash-001", 2, "error", "dash", 0),
            Event(content, progressive, "device-mp4-0001", 1, "startup", "progressive", 1500)
        }, "test-batch-" + Guid.NewGuid());

        await _store.WriteAsync(new[] { startup }, "test-batch-" + Guid.NewGuid());

        var summary = (await _store.SummaryAsync(1, 100)).Single(s => s.ContentId == content);

        Assert.Equal(new CoreSummary(content, 3, 3, 900, 1500, 1, 1200, 1, 1, 1, 0, 1, 1), summary);
    }

    [Fact]
    public async Task EventsFromThePlayer_ReachTheAdminsPlaybackPanel()
    {
        const string signingKey = "integration-media-signing-key-0123456789";
        const int content = 9;

        using var analytics = new AnalyticsHostFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = _infrastructure.KafkaBootstrapServers,
            ["Kafka:BatchLinger"] = "00:00:00.200",
            ["Kafka:PlaybackGroup"] = "playback-test-" + Guid.NewGuid().ToString("N"),
            ["ConnectionStrings:ClickHouse"] = _infrastructure.ClickHouseConnectionString
        });

        var analyticsServer = analytics.Server;

        using var blog = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["RateLimiting:ReadsPermitLimit"] = "100000",
            ["Media:Enabled"] = "true",
            ["Media:SigningKey"] = signingKey,
            ["Analytics:Address"] = analyticsServer.BaseAddress.ToString(),
            ["Analytics:OlapDeadline"] = "00:00:20"
        });

        using var wired = blog.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services
                .AddGrpcClient<AnalyticsReader.AnalyticsReaderClient>(options => options.Address = analyticsServer.BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => analyticsServer.CreateHandler())));

        using (var scope = wired.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO streamingservice.contentmedia (content_id, status, source_file, requested_at, attempts, storage_path, hls_path, dash_path, duration_seconds)
                VALUES ({content}, {MediaStatus.Ready}, 'panel.mp4', now(), 1, '9/20261014120000-00009999', 'master.m3u8', 'manifest.mpd', 300)
                ON CONFLICT (content_id) DO UPDATE SET status = EXCLUDED.status, storage_path = EXCLUDED.storage_path
                """);
        }

        var viewer = await SignedInAsync(wired);
        var plans = await wired.CreateClient().GetFromJsonAsync<List<PlanDto>>("/api/subscription/plans");

        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsJsonAsync("/api/me/streaming-profile", new
        {
            firstName = "Panel",
            lastName = "Viewer",
            dateOfBirth = "1991-02-03",
            gender = "Female",
            subscriptionPlanId = plans!.Last().SubscriptionPlanId
        })).StatusCode);

        var started = await viewer.PostAsJsonAsync("/api/v1/sessions", new { content_id = content, device_id = "panel-device-01" });
        var session = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetGuid();

        var sent = await viewer.PostAsJsonAsync($"/api/v1/sessions/{session}/events", new
        {
            events = new object[]
            {
                new { event_id = Guid.NewGuid(), seq = 1, type = "startup", duration_ms = 700, delivery = "hls-native" },
                new { event_id = Guid.NewGuid(), seq = 2, type = "stall", position = 31.5f, duration_ms = 450, delivery = "hls-native" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var admin = await SignedInAsync(wired, "Admin");
        JsonElement title = default;
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline)
        {
            var panel = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/playback?days=1");

            Assert.True(panel.GetProperty("enabled").GetBoolean());

            title = panel.GetProperty("titles").EnumerateArray().FirstOrDefault(t => t.GetProperty("contentId").GetInt32() == content);

            if (title.ValueKind == JsonValueKind.Object && title.GetProperty("stalls").GetInt64() == 1)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal(JsonValueKind.Object, title.ValueKind);
        Assert.Equal(700, title.GetProperty("medianStartupMs").GetDouble());
        Assert.Equal(450, title.GetProperty("stallMs").GetInt64());
        Assert.Equal(1, title.GetProperty("nativeHlsStartups").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(title.GetProperty("title").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/analytics/playback")).StatusCode);
    }

    private static PlaybackEventRow Event(
        int content, Guid session, string device, long seq, string kind, string delivery, int durationMs, int from = 0, int to = 0) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        DeviceId = device,
        Seq = seq,
        SessionId = session.ToString(),
        ContentId = content,
        Kind = kind,
        Delivery = delivery,
        OccurredAt = DateTime.UtcNow,
        Position = 1.5f,
        DurationMs = durationMs,
        FromHeight = from,
        ToHeight = to,
        Detail = string.Empty
    };

    private static async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"panel-{Guid.NewGuid():N}",
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
