using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Blog.Api.Services;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Media;
using Blog.Catalog.Domain.Media;
using Blog.Catalog.Infrastructure.Persistence;
using Blog.Posts.Infrastructure.Identity;
using Xunit;

namespace Blog.IntegrationTests.Catalog;

[Collection(InfrastructureCollection.Name)]
public class PlaybackApiTests : IAsyncLifetime
{
    private const string SigningKey = "integration-media-signing-key-0123456789";
    private const int PlayableContent = 4;

    private readonly InfrastructureFixture _infrastructure;
    private readonly string _incoming = Path.Combine(Path.GetTempPath(), "blog-incoming-" + Guid.NewGuid().ToString("N"));
    private ContainerApiFactory _factory = null!;
    private HttpClient _anonymous = null!;
    private HttpClient _internal = null!;

    public PlaybackApiTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_incoming);

        foreach (var name in new[] { "clip.mp4", "clip.English.vtt", "clip.Klingon.vtt", "other.mkv" })
        {
            await File.WriteAllTextAsync(Path.Combine(_incoming, name), "test " + name);
        }

        _factory = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["RateLimiting:ReadsPermitLimit"] = "100000",
            ["Media:Enabled"] = "true",
            ["Media:SigningKey"] = SigningKey,
            ["Media:IncomingPath"] = _incoming
        });

        _anonymous = _factory.CreateClient();
        _internal = _factory.CreateClient();
        _internal.DefaultRequestHeaders.Add(InternalKeys.Header, InternalKeys.Derive(SigningKey));
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        try
        {
            Directory.Delete(_incoming, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task APackagingJob_IsHandedOutOnce_AndItsReportBecomesThePlayableTitle()
    {
        var admin = await SignedInAsync("Admin");
        var queued = await admin.PostAsJsonAsync("/api/content/6/media", new { sourceFile = "clip.mp4" });

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Equal(MediaStatus.Queued, (await queued.Content.ReadFromJsonAsync<MediaInfoDto>())!.Status);

        var job = await ClaimUntilAsync(6);

        Assert.Equal("clip.mp4", job.GetProperty("source_file").GetString());
        Assert.Equal(120, job.GetProperty("claim_seconds").GetInt32());

        var subtitle = job.GetProperty("subtitles").EnumerateArray().Single();

        Assert.Equal("clip.English.vtt", subtitle.GetProperty("file").GetString());
        Assert.Equal("English", subtitle.GetProperty("language").GetString());
        Assert.Equal("en", subtitle.GetProperty("tag").GetString());

        var attempts = job.GetProperty("attempts").GetInt32();
        var english = subtitle.GetProperty("language_id").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await _internal.PostAsync("/internal/v1/packaging/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _internal.PostAsJsonAsync("/internal/v1/packaging/6/extend",
            new { attempts, source_file = "clip.mp4" })).StatusCode);

        var report = Report(6, attempts, "6/20261014120000-0000abcd", english);

        Assert.Equal(HttpStatusCode.NoContent, (await _internal.PostAsJsonAsync("/internal/v1/packaging/6/done", report)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _internal.PostAsJsonAsync("/internal/v1/packaging/6/done", report)).StatusCode);

        var media = await _anonymous.GetFromJsonAsync<MediaInfoDto>("/api/content/6/media");

        Assert.True(media!.Playable);
        Assert.Equal(MediaStatus.Ready, media.Status);
        Assert.Equal(new[] { MediaFormats.Hls, MediaFormats.Dash, MediaFormats.Progressive }, media.Formats);
        Assert.Equal(new[] { RenditionKinds.Video, RenditionKinds.Video, RenditionKinds.Audio, RenditionKinds.Progressive },
            media.Renditions.Select(r => r.Kind));
        Assert.Equal("English", media.Subtitles.Single().Label);
        Assert.Null(media.SourceFile);

        await using var db = Db();

        var stored = await db.ContentMedia.AsNoTracking().SingleAsync(m => m.ContentId == 6);
        var subtitleFile = await (
            from file in db.SubtitleFiles
            join row in db.Subtitles on file.SubtitleId equals row.SubtitleId
            where row.ContentId == 6
            select new { file.Uri, row.LanguageId }).SingleAsync();

        Assert.Equal("6/20261014120000-0000abcd", stored.StoragePath);
        Assert.Null(stored.ClaimedUntil);
        Assert.Equal(600f, stored.DurationSeconds);
        Assert.Equal(("subs/2.vtt", english), (subtitleFile.Uri, subtitleFile.LanguageId));
    }

    [Fact]
    public async Task TheInternalEndpoints_WantTheDerivedKey()
    {
        var stranger = _factory.CreateClient();

        stranger.DefaultRequestHeaders.Add(InternalKeys.Header, InternalKeys.Derive("some-other-signing-key-0123456789"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsync("/internal/v1/packaging/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync("/internal/v1/packaging/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stranger.GetAsync($"/internal/v1/media/sessions/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _internal.GetAsync($"/internal/v1/media/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Queuing_NeedsAnAdminAndAFileFromTheIncomingFolder()
    {
        var admin = await SignedInAsync("Admin");
        var (reader, _) = await WithProfileAsync();

        var inbox = (await admin.GetFromJsonAsync<List<InboxFileDto>>("/api/media/inbox"))!;

        Assert.Equal(new[] { "clip.mp4", "other.mkv" }, inbox.Select(f => f.File));
        Assert.Equal(new[] { ("English", true), ("Klingon", false) }, inbox[0].Subtitles.Select(s => (s.Language, s.Known)));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/content/7/media", new { sourceFile = "missing.mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/content/7/media", new { sourceFile = "../clip.mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/content/7/media", new { sourceFile = "clip.English.vtt" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/content/999999/media", new { sourceFile = "clip.mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/content/7/media", new { sourceFile = "clip.mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsJsonAsync("/api/content/7/media", new { sourceFile = "clip.mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/media/inbox")).StatusCode);
    }

    [Fact]
    public async Task AFailedJob_ShowsItsErrorToTheAdminOnly()
    {
        var admin = await SignedInAsync("Admin");

        await admin.PostAsJsonAsync("/api/content/8/media", new { sourceFile = "other.mkv" });

        var job = await ClaimUntilAsync(8);

        Assert.Equal(HttpStatusCode.NoContent, (await _internal.PostAsJsonAsync("/internal/v1/packaging/8/failed",
            new { attempts = job.GetProperty("attempts").GetInt32(), source_file = "other.mkv", error = "ffmpeg said no" })).StatusCode);

        var forAdmin = await admin.GetFromJsonAsync<MediaInfoDto>("/api/content/8/media");
        var forReader = await _anonymous.GetFromJsonAsync<MediaInfoDto>("/api/content/8/media");

        Assert.Equal(MediaStatus.Failed, forAdmin!.Status);
        Assert.Equal("ffmpeg said no", forAdmin.Error);
        Assert.Equal("other.mkv", forAdmin.SourceFile);
        Assert.False(forReader!.Playable);
        Assert.Null(forReader.Error);
    }

    [Fact]
    public async Task ASession_IsSignedForOnePackage_AndRepeatingTheStartReturnsIt()
    {
        await EnsurePackagedAsync();

        var (viewer, _) = await WithProfileAsync();
        var first = await StartAsync(viewer, "device-a-0001");
        var again = await StartAsync(viewer, "device-a-0001");

        Assert.Equal(HttpStatusCode.OK, first.Status);

        var session = first.Body.GetProperty("session_id").GetGuid();
        var playlist = first.Body.GetProperty("playlist_url").GetString()!;
        var token = playlist.Split('/')[2];

        Assert.Equal(session, again.Body.GetProperty("session_id").GetGuid());
        Assert.True(again.Body.GetProperty("resumed").GetBoolean());
        Assert.Equal(playlist, again.Body.GetProperty("playlist_url").GetString());
        Assert.StartsWith("/vod/", playlist);
        Assert.EndsWith("/master.m3u8", playlist);
        Assert.Equal($"/vod/{token}/manifest.mpd", first.Body.GetProperty("manifest_url").GetString());
        Assert.Equal($"/vod/{token}/progressive.mp4", first.Body.GetProperty("progressive_url").GetString());
        Assert.Equal($"/vod/{token}/subs/2.vtt", first.Body.GetProperty("subtitles")[0].GetProperty("url").GetString());
        Assert.Equal(60, first.Body.GetProperty("lease_seconds").GetInt32());
        Assert.Equal(20, first.Body.GetProperty("heartbeat_seconds").GetInt32());
        Assert.Equal(1, first.Body.GetProperty("max_streams").GetInt32());

        Assert.True(MediaToken.TryRead(System.Text.Encoding.UTF8.GetBytes(SigningKey), token, out var grant));
        Assert.Equal(session, grant.SessionId);
        Assert.Equal(MediaToken.ScopeOf($"{PlayableContent}/20261014120000-00001234"), grant.Scope);

        var access = await _internal.GetFromJsonAsync<JsonElement>($"/internal/v1/media/sessions/{session}");

        Assert.Equal($"{PlayableContent}/20261014120000-00001234", access.GetProperty("storage_path").GetString());
        Assert.InRange(
            access.GetProperty("live_until").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 50, 61);
    }

    [Fact]
    public async Task ThePlansStreamCount_LimitsDevices_AndEndingASessionFreesItsSlot()
    {
        await EnsurePackagedAsync();

        var (viewer, _) = await WithProfileAsync();
        var first = await StartAsync(viewer, "device-a-0001");
        var second = await StartAsync(viewer, "device-b-0002");

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(PlaybackService.LimitReached(1, 1), second.Text);

        var session = first.Body.GetProperty("session_id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await viewer.DeleteAsync($"/api/v1/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.DeleteAsync($"/api/v1/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await _internal.GetAsync($"/internal/v1/media/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(viewer, "device-b-0002")).Status);
    }

    [Fact]
    public async Task Positions_KeepTheNewestSeq_AndTheNextSessionResumesThere()
    {
        await EnsurePackagedAsync();

        var (viewer, _) = await WithProfileAsync();
        var session = (await StartAsync(viewer, "device-a-0001")).Body.GetProperty("session_id").GetGuid();

        var first = await PutAsync(viewer, $"/api/v1/sessions/{session}/position", new { position = 120.5f, seq = 3 });
        var stale = await PutAsync(viewer, $"/api/v1/sessions/{session}/position", new { position = 30f, seq = 2 });

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(120.5f, stale.Body.GetProperty("position").GetSingle());
        Assert.Equal(3, stale.Body.GetProperty("seq").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(viewer, $"/api/v1/sessions/{session}/position", new { position = 5f })).Status);

        await viewer.DeleteAsync($"/api/v1/sessions/{session}");

        Assert.Equal(HttpStatusCode.Gone, (await PutAsync(viewer, $"/api/v1/sessions/{session}/position", new { position = 140f, seq = 4 })).Status);

        var next = await StartAsync(viewer, "device-a-0001");

        Assert.NotEqual(session, next.Body.GetProperty("session_id").GetGuid());
        Assert.Equal(120.5f, next.Body.GetProperty("position").GetSingle());

        var (stranger, _) = await WithProfileAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await PutAsync(stranger, $"/api/v1/sessions/{next.Body.GetProperty("session_id").GetGuid()}/position", new { position = 1f, seq = 1 })).Status);
    }

    [Fact]
    public async Task CompletingTwice_WritesOneHistoryRow_AndTheTriggersRunOnce()
    {
        await EnsurePackagedAsync();

        var (viewer, profile) = await WithProfileAsync();
        var session = (await StartAsync(viewer, "device-a-0001")).Body.GetProperty("session_id").GetGuid();
        var completionId = Guid.NewGuid();

        await PutAsync(viewer, $"/api/v1/sessions/{session}/position", new { position = 590f, seq = 9 });

        var first = await PostAsync(viewer, $"/api/v1/sessions/{session}/complete", new { completion_id = completionId });
        var second = await PostAsync(viewer, $"/api/v1/sessions/{session}/complete", new { completion_id = completionId });
        var other = await PostAsync(viewer, $"/api/v1/sessions/{session}/complete", new { completion_id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(first.Body.GetProperty("repeated").GetBoolean());
        Assert.True(second.Body.GetProperty("repeated").GetBoolean());
        Assert.Equal(first.Body.GetProperty("watched_content_id").GetInt32(), second.Body.GetProperty("watched_content_id").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, other.Status);

        await using var db = Db();

        var history = await db.WatchHistory.AsNoTracking()
            .Where(w => w.UserId == profile.UserId && w.ContentId == PlayableContent)
            .ToListAsync();

        Assert.Single(history);
        Assert.Equal(590f, history[0].Timestamp);
        Assert.True(await db.Recommendations.AnyAsync(r => r.UserId == profile.UserId));
        Assert.False(await db.PlaybackPositions.AnyAsync(p => p.UserId == profile.UserId && p.ContentId == PlayableContent));

        var rewatch = await StartAsync(viewer, "device-a-0001");

        Assert.NotEqual(session, rewatch.Body.GetProperty("session_id").GetGuid());
        Assert.Equal(0f, rewatch.Body.GetProperty("position").GetSingle());
    }

    [Fact]
    public async Task ALapsedSubscription_CannotStartAndSaysWhy()
    {
        await EnsurePackagedAsync();

        var (viewer, profile) = await WithProfileAsync();

        await using (var db = Db())
        {
            await db.Users.Where(u => u.UserId == profile.UserId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.SubscriptionEndDate, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
        }

        var refused = await StartAsync(viewer, "device-a-0001");

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(PlaybackService.SubscriptionInactive, refused.Text);
    }

    [Fact]
    public async Task StartingNeedsAProfile_ADevice_AndAPackagedTitle()
    {
        await EnsurePackagedAsync();

        var (profileless, _) = await SignedInClientAsync();
        var (viewer, _) = await WithProfileAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await StartAsync(_anonymous, "device-a-0001")).Status);
        Assert.Equal(StreamingAccountService.ProfileRequired, (await StartAsync(profileless, "device-a-0001")).Text);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(viewer, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(viewer, "device-a-0001", contentId: 12)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(viewer, "/api/v1/sessions", new { device_id = "device-a-0001" })).Status);
    }

    [Fact]
    public async Task Events_AreAcceptedForTheViewersOwnSessionOnly()
    {
        await EnsurePackagedAsync();

        var (viewer, _) = await WithProfileAsync();
        var (stranger, _) = await WithProfileAsync();
        var session = (await StartAsync(viewer, "device-a-0001")).Body.GetProperty("session_id").GetGuid();
        var batch = new
        {
            events = new object[]
            {
                new { event_id = Guid.NewGuid(), seq = 1, type = "startup", at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), duration_ms = 640, delivery = "hls" },
                new { event_id = Guid.NewGuid(), seq = 2, type = "quality", position = 4.2f, from = 360, to = 720, delivery = "hls" },
                new { event_id = Guid.NewGuid(), seq = 3, type = "teleport" }
            }
        };

        var accepted = await PostAsync(viewer, $"/api/v1/sessions/{session}/events", batch);

        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(2, accepted.Body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, accepted.Body.GetProperty("rejected").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(stranger, $"/api/v1/sessions/{session}/events", batch)).Status);
    }

    private async Task EnsurePackagedAsync()
    {
        var current = await _anonymous.GetAsync($"/api/content/{PlayableContent}/media");

        if (current.IsSuccessStatusCode && (await current.Content.ReadFromJsonAsync<MediaInfoDto>())!.Playable)
        {
            return;
        }

        var admin = await SignedInAsync("Admin");

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/content/{PlayableContent}/media", new { sourceFile = "clip.mp4" })).StatusCode);

        var job = await ClaimUntilAsync(PlayableContent);
        var english = job.GetProperty("subtitles")[0].GetProperty("language_id").GetInt32();
        var done = await _internal.PostAsJsonAsync(
            $"/internal/v1/packaging/{PlayableContent}/done",
            Report(PlayableContent, job.GetProperty("attempts").GetInt32(), $"{PlayableContent}/20261014120000-00001234", english));

        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
    }

    private async Task<JsonElement> ClaimUntilAsync(int contentId)
    {
        for (var tries = 0; tries < 20; tries++)
        {
            var response = await _internal.PostAsync("/internal/v1/packaging/claim", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var job = await response.Content.ReadFromJsonAsync<JsonElement>();
            var claimed = job.GetProperty("content_id").GetInt32();

            if (claimed == contentId)
            {
                return job;
            }

            await _internal.PostAsJsonAsync($"/internal/v1/packaging/{claimed}/failed", new
            {
                attempts = job.GetProperty("attempts").GetInt32(),
                source_file = job.GetProperty("source_file").GetString(),
                error = "cleared by a test"
            });
        }

        throw new InvalidOperationException($"Title {contentId} was never handed out.");
    }

    private static object Report(int contentId, int attempts, string storage, int languageId) => new
    {
        attempts,
        source_file = "clip.mp4",
        storage_path = storage,
        hls_path = "master.m3u8",
        dash_path = "manifest.mpd",
        duration_seconds = 600f,
        renditions = new object[]
        {
            new { kind = "video", codec = "avc1.64001f", bitrate = 2_900_000, width = 1280, height = 720, path = "video-0.m3u8" },
            new { kind = "video", codec = "avc1.64001e", bitrate = 820_000, width = 640, height = 360, path = "video-1.m3u8" },
            new { kind = "audio", codec = "mp4a.40.2", bitrate = 130_000, language = "und", path = "audio-2.m3u8" },
            new { kind = "progressive", codec = "avc1.64001e,mp4a.40.2", bitrate = 950_000, width = 640, height = 360, path = "progressive.mp4" }
        },
        subtitles = new[] { new { language_id = languageId, uri = $"subs/{languageId}.vtt" } }
    };

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, string Text);

    private static async Task<Reply> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var body = response.IsSuccessStatusCode && text.Length > 0 && text[0] == '{'
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;

        return new Reply(response.StatusCode, body, text);
    }

    private static async Task<Reply> StartAsync(HttpClient client, string deviceId, int contentId = PlayableContent) =>
        await ReadAsync(await client.PostAsJsonAsync("/api/v1/sessions", new { content_id = contentId, device_id = deviceId }));

    private static async Task<Reply> PostAsync(HttpClient client, string path, object body) =>
        await ReadAsync(await client.PostAsJsonAsync(path, body));

    private static async Task<Reply> PutAsync(HttpClient client, string path, object body) =>
        await ReadAsync(await client.PutAsJsonAsync(path, body));

    private async Task<(HttpClient Client, StreamingProfileDto Profile)> WithProfileAsync()
    {
        var (client, _) = await SignedInClientAsync();
        var plans = await _anonymous.GetFromJsonAsync<List<PlanDto>>("/api/subscription/plans");
        var basic = plans!.Single(p => PlanLimits.MaxStreams(p.PlanInfo) == 1);
        var response = await client.PostAsJsonAsync("/api/me/streaming-profile", new
        {
            firstName = "Test",
            lastName = "Viewer",
            dateOfBirth = "1990-05-17",
            gender = "Other",
            subscriptionPlanId = basic.SubscriptionPlanId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (client, (await response.Content.ReadFromJsonAsync<StreamingProfileDto>())!);
    }

    private async Task<HttpClient> SignedInAsync(params string[] roles) => (await SignedInClientAsync(roles)).Client;

    private async Task<(HttpClient Client, string IdentityId)> SignedInClientAsync(params string[] roles)
    {
        using var scope = _factory.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"viewer-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}"[..20] + "@example.test"
        };

        var created = await users.CreateAsync(user, "Str0ng!Passw0rd");

        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, roles);
        var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, user.Id);
    }

    private StreamingDbContext Db() =>
        _factory.CreateScope().ServiceProvider.GetRequiredService<StreamingDbContext>();
}
