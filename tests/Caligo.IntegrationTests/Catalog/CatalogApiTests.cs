using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Caligo.Api.Services;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Application.Insights;
using Caligo.Catalog.Application.Music;
using Caligo.Catalog.Infrastructure.Persistence;
using Caligo.Posts.Application.Posts;
using Caligo.Posts.Infrastructure.Identity;
using Caligo.SharedKernel;
using Xunit;

namespace Caligo.IntegrationTests.Catalog;

[Collection(InfrastructureCollection.Name)]
public class CatalogApiTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ContainerApiFactory _factory = null!;
    private HttpClient _anonymous = null!;

    public CatalogApiTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _factory = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["RateLimiting:ReadsPermitLimit"] = "100000"
        });
        _anonymous = _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheSampleCatalog_IsPagedWithoutGapsOrRepeats()
    {
        var seen = new List<int>();
        string? cursor = null;

        do
        {
            var url = "/api/content?limit=10" + (cursor is null ? string.Empty : "&cursor=" + cursor);
            var page = await _anonymous.GetFromJsonAsync<PagedResult<ContentSummaryDto>>(url);

            Assert.NotNull(page);
            Assert.True(page!.Items.Count <= 10);

            seen.AddRange(page.Items.Select(i => i.ContentId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.All(Enumerable.Range(1, 27), id => Assert.Contains(id, seen));
    }

    [Theory]
    [InlineData(ContentKinds.Movie, 20)]
    [InlineData(ContentKinds.Episode, 7)]
    public async Task Filtering_ByKind_UsesTheSubtypeTables(string kind, int expected)
    {
        var page = await _anonymous.GetFromJsonAsync<PagedResult<ContentSummaryDto>>($"/api/content?kind={kind}&limit=50");

        Assert.Equal(expected, page!.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(kind, item.Kind));
    }

    [Fact]
    public async Task AnUnknownKind_IsABadRequest()
    {
        var response = await _anonymous.GetAsync("/api/content?kind=podcast");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ATitle_ShowsWhatTheSchemaKnowsAboutIt()
    {
        var detail = await _anonymous.GetFromJsonAsync<ContentDetailDto>("/api/content/3");

        await using var db = Db();

        var stored = await db.Contents.AsNoTracking().SingleAsync(c => c.ContentId == 3);

        Assert.Equal(stored.Title, detail!.Title);
        Assert.Equal(ContentKinds.Movie, detail.Kind);
        Assert.NotNull(detail.Movie);
        Assert.NotEmpty(detail.Genres);
        Assert.NotEmpty(detail.Actors);
        Assert.False(string.IsNullOrWhiteSpace(detail.ProductionCompany.Name));
        Assert.Equal(HttpStatusCode.NotFound, (await _anonymous.GetAsync("/api/content/999999")).StatusCode);
    }

    [Fact]
    public async Task SimilarTitles_ComeFromTheSchemasOwnFunction()
    {
        var similar = await _anonymous.GetFromJsonAsync<List<SimilarContentDto>>("/api/content/3/similar");

        Assert.Equal(CatalogService.SimilarCount, similar!.Count);
        Assert.DoesNotContain(similar, s => s.ContentId == 3);
        Assert.Equal(similar.OrderByDescending(s => s.Similarity).Select(s => s.Similarity), similar.Select(s => s.Similarity));
        Assert.Equal(0.5f, similar[0].Similarity, 4);
    }

    [Fact]
    public async Task ASeries_HasItsSeasonsAndEpisodes()
    {
        var list = await _anonymous.GetFromJsonAsync<List<SeriesSummaryDto>>("/api/series");
        var series = await _anonymous.GetFromJsonAsync<SeriesDetailDto>("/api/series/1");

        Assert.Equal(10, list!.Count);
        Assert.Equal(5, series!.Seasons.Count);
        Assert.Equal(7, series.Seasons.Single(s => s.SeasonNumber == 1).Episodes.Count);
        Assert.Equal(
            Enumerable.Range(1, 7),
            series.Seasons.Single(s => s.SeasonNumber == 1).Episodes.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public async Task AStreamingProfile_IsCreatedOnce_WithItsFirstPayment()
    {
        var (client, _) = await SignedInAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me/streaming-profile")).StatusCode);

        var plans = await _anonymous.GetFromJsonAsync<List<PlanDto>>("/api/subscription/plans");
        var plan = plans!.First();

        var created = await client.PostAsJsonAsync("/api/me/streaming-profile", ProfileRequest(plan.SubscriptionPlanId));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var profile = await created.Content.ReadFromJsonAsync<StreamingProfileDto>();

        Assert.True(profile!.Active);
        Assert.Equal("Other", profile.Gender);

        var again = await client.PostAsJsonAsync("/api/me/streaming-profile", ProfileRequest(plan.SubscriptionPlanId));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var spending = await client.GetFromJsonAsync<SpendingDto>("/api/me/spending");

        Assert.Single(spending!.Payments);
        Assert.Equal(plan.MonthlyPrice, spending.AllTimeSpending, 2);

        await using var db = Db();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.UserId == profile.UserId);

        Assert.Equal(Caligo.Catalog.Domain.Users.StreamingUser.ExternalPassword, user.Password);
    }

    [Fact]
    public async Task Renewing_AddsAPaymentAndExtendsTheSubscription()
    {
        var (client, profile) = await WithProfileAsync();

        var response = await client.PostAsJsonAsync(
            "/api/me/subscription", new { subscriptionPlanId = profile.SubscriptionPlanId });

        var renewed = await response.Content.ReadFromJsonAsync<StreamingProfileDto>();
        var spending = await client.GetFromJsonAsync<SpendingDto>("/api/me/spending");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(renewed!.SubscriptionEndDate > profile.SubscriptionEndDate);
        Assert.Equal(2, spending!.Payments.Count);
    }

    [Fact]
    public async Task Rating_FiresTheAverageTrigger_AndReratingKeepsOneRow()
    {
        var (client, profile) = await WithProfileAsync();

        const int contentId = 10;

        var first = await client.PutAsJsonAsync($"/api/content/{contentId}/interaction", new { liked = true, rating = 8f });
        var firstBody = await first.Content.ReadFromJsonAsync<InteractionDto>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await AverageAsync(contentId), firstBody!.AverageRating!.Value, 3);

        var second = await client.PutAsJsonAsync($"/api/content/{contentId}/interaction", new { rating = 2f });
        var secondBody = await second.Content.ReadFromJsonAsync<InteractionDto>();

        await using var db = Db();

        var rows = await db.Interactions.AsNoTracking()
            .Where(i => i.ContentId == contentId && i.UserId == profile.UserId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(2f, rows[0].Rating);
        Assert.Null(rows[0].Liked);
        Assert.Equal(await AverageAsync(contentId), secondBody!.AverageRating!.Value, 3);

        var mine = await client.GetFromJsonAsync<InteractionDto>($"/api/content/{contentId}/interaction");

        Assert.Equal(2f, mine!.Rating);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/content/{contentId}/interaction")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/content/{contentId}/interaction")).StatusCode);
    }

    [Fact]
    public async Task LoggingAWatch_RebuildsTheRecommendationsThroughTheTrigger()
    {
        var (client, _) = await WithProfileAsync();

        var watched = await client.PostAsJsonAsync("/api/content/3/watch", new { timestamp = 125f });

        Assert.Equal(HttpStatusCode.OK, watched.StatusCode);

        var recommendations = await client.GetFromJsonAsync<List<RecommendationDto>>("/api/me/recommendations");
        var history = await client.GetFromJsonAsync<List<WatchDto>>("/api/me/history");

        Assert.Equal(10, recommendations!.Count);
        Assert.All(recommendations, r => Assert.Equal("Jaccard index", r.Reason));
        Assert.DoesNotContain(recommendations, r => r.ContentId == 3);
        Assert.Equal(3, history![0].ContentId);
        Assert.Equal(125f, history[0].Timestamp);
        Assert.Empty((await client.GetFromJsonAsync<List<NotificationDto>>("/api/me/notifications"))!);
    }

    [Fact]
    public async Task AComment_ShowsUpUnderTheTitle()
    {
        var (client, _) = await WithProfileAsync();
        var text = "Integration comment " + Guid.NewGuid().ToString("N");

        var posted = await client.PostAsJsonAsync("/api/content/5/comments", new { comment = text });
        var comments = await _anonymous.GetFromJsonAsync<List<CommentDto>>("/api/content/5/comments?limit=5");

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(text, comments![0].Comment);
        Assert.Equal("Test Viewer", comments[0].Author);
    }

    [Fact]
    public async Task ActivityWithoutAProfile_IsAConflict_AndAnonymousIsUnauthorized()
    {
        var (client, _) = await SignedInAsync();

        var withoutProfile = await client.PutAsJsonAsync("/api/content/3/interaction", new { rating = 5f });
        var anonymous = await _anonymous.PutAsJsonAsync("/api/content/3/interaction", new { rating = 5f });

        Assert.Equal(HttpStatusCode.Conflict, withoutProfile.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task TheViews_AreOpen_AndTheTenQueriesAreForAdministrators()
    {
        var (reader, _) = await SignedInAsync();
        var (admin, _) = await SignedInAsync("Admin");

        var popular = await _anonymous.GetFromJsonAsync<List<PopularContentDto>>("/api/insights/popular");
        var engaging = await _anonymous.GetFromJsonAsync<List<EngagingContentDto>>("/api/insights/engaging");

        Assert.Equal(10, popular!.Count);
        Assert.NotEmpty(engaging!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/insights/queries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/insights/active-users")).StatusCode);

        var queries = (await admin.GetFromJsonAsync<List<SavedQueryDto>>("/api/insights/queries"))!;

        Assert.Equal(Enumerable.Range(1, 10), queries.Select(q => q.Number));
        Assert.Equal("Find the highest payment made by a user", queries[2].Title);

        foreach (var query in queries)
        {
            var response = await admin.GetAsync($"/api/insights/queries/{query.Number}");

            Assert.True(response.IsSuccessStatusCode, $"query {query.Number}: {await response.Content.ReadAsStringAsync()}");
        }

        var highest = await admin.GetFromJsonAsync<QueryResultDto>("/api/insights/queries/3");

        Assert.Equal(new[] { "user_name", "highest_payment" }, highest!.Columns);
        Assert.True(highest.Rows.Count >= 20);
        Assert.Equal(10, (await admin.GetFromJsonAsync<List<ActiveUserDto>>("/api/insights/active-users"))!.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/insights/queries/99")).StatusCode);
    }

    [Fact]
    public async Task AReview_IsAPostLinkedToATitle_AndIsListedUnderIt()
    {
        var (writer, _) = await SignedInAsync("Writer");

        const int contentId = 7;

        var title = "Review " + Guid.NewGuid().ToString("N");

        var created = await writer.PostAsJsonAsync(
            "/api/blogposts", new { title, content = "Worth every minute.", tags = new[] { "review" }, contentId });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var post = await created.Content.ReadFromJsonAsync<PostDetailDto>();

        await using var db = Db();

        var stored = await db.Contents.AsNoTracking().SingleAsync(c => c.ContentId == contentId);

        Assert.Equal(contentId, post!.ContentId);
        Assert.Equal(stored.Title, post.ContentTitle);

        var reviews = await _anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>($"/api/blogposts?contentId={contentId}");
        var review = Assert.Single(reviews!.Items, r => r.Id == post.Id);

        Assert.Equal(stored.Title, review.ContentTitle);
        Assert.All(reviews.Items, r => Assert.Equal(contentId, r.ContentId));

        var elsewhere = await _anonymous.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/blogposts?contentId=8");

        Assert.DoesNotContain(elsewhere!.Items, r => r.Id == post.Id);

        var read = await _anonymous.GetFromJsonAsync<PostDetailDto>($"/api/blogposts/{post.Id}");

        Assert.Equal(stored.Title, read!.ContentTitle);

        var unknown = await writer.PostAsJsonAsync(
            "/api/blogposts", new { title = title + " 2", content = "Nothing to review.", tags = Array.Empty<string>(), contentId = 999999 });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var scope = _factory.CreateScope();

        var outbox = await scope.ServiceProvider.GetRequiredService<Caligo.Posts.Infrastructure.Persistence.ApplicationDbContext>()
            .OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.PartitionKey == post.Id.ToString() && m.EventType == "post.published");

        using var payload = System.Text.Json.JsonDocument.Parse(outbox.Payload);

        Assert.Equal(contentId, payload.RootElement.GetProperty("contentId").GetInt32());
    }

    [Fact]
    public async Task MusicImport_ReadsCaligosFolders_AndRunningItAgainAddsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "caligo-import-" + Guid.NewGuid().ToString("N"));
        var artist = "Test Artist " + Guid.NewGuid().ToString("N")[..8];

        try
        {
            WriteLibrary(root, artist);

            using var factory = new ContainerApiFactory(_infrastructure, new Dictionary<string, string?>
            {
                ["Catalog:CaligoPublicRoot"] = root,
                ["Catalog:SyncMusic"] = "false",
                ["RateLimiting:ReadsPermitLimit"] = "100000"
            });

            var (admin, _) = await SignedInAsync(factory, "Admin");

            var first = await admin.PostAsync("/api/music/import", null);
            var firstResult = await first.Content.ReadFromJsonAsync<MusicImportResult>();

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Empty(firstResult!.Problems);
            Assert.Equal(3, firstResult.SongsAdded);
            Assert.Equal(1, firstResult.SongsAlreadyThere);
            Assert.Equal(1, firstResult.ArtistsAdded);
            Assert.Equal(1, firstResult.AlbumsAdded);

            var second = await (await admin.PostAsync("/api/music/import", null)).Content.ReadFromJsonAsync<MusicImportResult>();

            Assert.Equal(0, second!.SongsAdded);
            Assert.Equal(0, second.AlbumsAdded);
            Assert.Equal(4, second.SongsAlreadyThere);

            var client = factory.CreateClient();
            var artists = await client.GetFromJsonAsync<List<ArtistSummaryDto>>("/api/artists?query=" + Uri.EscapeDataString(artist));
            var found = Assert.Single(artists!);

            Assert.Equal(3, found.Songs);

            var detail = await client.GetFromJsonAsync<ArtistDetailDto>($"/api/artists/{found.ArtistId}");
            var album = await client.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{detail!.Albums.Single().AlbumId}");

            Assert.Equal(new[] { "Second Song", "First Song", "Radio Only" }, album!.Tracks.Select(t => t.Title));
            Assert.Equal(new int?[] { 1, 2, 3 }, album.Tracks.Select(t => t.TrackNumber));
            Assert.Equal("/mixtape/test-album/first%20song.mp3", album.Tracks[1].AudioUri);
            Assert.Equal("/mixtape/test-album/cover.png", album.CoverUri);
            Assert.Equal(new DateOnly(2013, 1, 1), album.ReleaseDate);
            Assert.Equal(0.1f, album.Tracks[0].Duration, 2);

            var song = await client.GetFromJsonAsync<ContentDetailDto>($"/api/content/{album.Tracks[1].ContentId}");

            Assert.Equal(ContentKinds.Song, song!.Kind);
            Assert.Equal(new DateOnly(2013, 9, 9), song.ReleaseDate);
            Assert.Equal("Test Label", song.ProductionCompany.Name);
            Assert.Contains(song.Genres, g => g.Name == "Indie Rock");
            Assert.Equal(artist, song.Song!.Artists.Single().Name);

            var untagged = await client.GetFromJsonAsync<ContentDetailDto>($"/api/content/{album.Tracks[2].ContentId}");

            Assert.Equal(new DateOnly(2013, 1, 1), untagged!.ReleaseDate);
            Assert.Equal("Unknown label", untagged.ProductionCompany.Name);
            Assert.Empty(untagged.Genres);

            var lookup = await client.GetFromJsonAsync<SongMatchDto>(
                $"/api/songs/lookup?artist={Uri.EscapeDataString(artist.ToUpperInvariant())}&title=first%20song");

            Assert.Equal(song.ContentId, lookup!.ContentId);
            Assert.Equal("First Song", lookup.Title);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
                $"/api/songs/lookup?artist={Uri.EscapeDataString(artist)}&title=never%20recorded")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/songs/lookup?artist=only")).StatusCode);

            var songs = await client.GetFromJsonAsync<PagedResult<ContentSummaryDto>>("/api/content?kind=song&limit=50");

            Assert.Contains(songs!.Items, s => s.ContentId == song.ContentId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteLibrary(string root, string artist)
    {
        var album = Path.Combine(root, "mixtape", "test-album");
        var music = Path.Combine(root, "music");

        Directory.CreateDirectory(album);
        Directory.CreateDirectory(music);

        File.WriteAllBytes(Path.Combine(album, "first song.mp3"), Mp3(seconds: 6, track: "2/2", recorded: "2013-09-09"));
        File.WriteAllBytes(Path.Combine(album, "second-song.mp3"), Mp3(seconds: 6, track: "1"));
        File.WriteAllBytes(Path.Combine(album, "cover.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(album, "metadata.json"), $$"""
            {
              "albumTitle": "Test Album",
              "albumArtist": "{{artist}}",
              "coverFile": "cover.png",
              "tracks": [
                { "file": "first song.mp3", "title": "First Song" },
                { "file": "second-song.mp3", "title": "Second Song" }
              ]
            }
            """);

        File.WriteAllBytes(Path.Combine(music, "aaaa.mp3"), Mp3(seconds: 6));
        File.WriteAllBytes(Path.Combine(music, "bbbb.mp3"), Mp3(seconds: 6, tagged: false));
        File.WriteAllText(Path.Combine(music, "playlist.json"), $$"""
            [
              { "file": "aaaa.mp3", "artist": "{{artist}}", "album": "Test Album", "title": "first song" },
              { "file": "bbbb.mp3", "artist": "{{artist}}", "album": "Test Album", "title": "Radio Only" }
            ]
            """);
    }

    private static byte[] Mp3(int seconds, string? track = null, string? recorded = null, bool tagged = true)
    {
        using var stream = new MemoryStream();

        var frames = new List<byte>();

        void Text(string id, string value)
        {
            var body = Encoding.Latin1.GetBytes(value);

            frames.AddRange(Encoding.ASCII.GetBytes(id));
            frames.AddRange(new byte[] { 0, 0, 0, (byte)(body.Length + 1), 0, 0, 0 });
            frames.AddRange(body);
        }

        if (tagged)
        {
            Text("TCON", "Indie Rock");
            Text("TYER", "2013");
            Text("TPUB", "Test Label");
        }

        if (track is not null)
        {
            Text("TRCK", track);
        }

        if (recorded is not null)
        {
            Text("TDRC", recorded);
        }

        stream.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, (byte)frames.Count });
        stream.Write(frames.ToArray());

        var frame = new byte[417];

        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;

        for (var i = 0; i < seconds * 16000 / frame.Length; i++)
        {
            stream.Write(frame);
        }

        return stream.ToArray();
    }

    private static object ProfileRequest(int planId) => new
    {
        firstName = "Test",
        lastName = "Viewer",
        dateOfBirth = "1990-05-17",
        gender = "Other",
        subscriptionPlanId = planId
    };

    private async Task<(HttpClient Client, StreamingProfileDto Profile)> WithProfileAsync()
    {
        var (client, _) = await SignedInAsync();
        var plans = await _anonymous.GetFromJsonAsync<List<PlanDto>>("/api/subscription/plans");
        var response = await client.PostAsJsonAsync("/api/me/streaming-profile", ProfileRequest(plans![0].SubscriptionPlanId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (client, (await response.Content.ReadFromJsonAsync<StreamingProfileDto>())!);
    }

    private Task<(HttpClient Client, string IdentityId)> SignedInAsync(params string[] roles) =>
        SignedInAsync(_factory, roles);

    private static async Task<(HttpClient Client, string IdentityId)> SignedInAsync(
        ContainerApiFactory factory, params string[] roles)
    {
        using var scope = factory.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = $"viewer-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test"
        };

        var created = await users.CreateAsync(user, "Str0ng!Passw0rd");

        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, roles);
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, user.Id);
    }

    private StreamingDbContext Db() =>
        _factory.CreateScope().ServiceProvider.GetRequiredService<StreamingDbContext>();

    private async Task<float> AverageAsync(int contentId)
    {
        await using var db = Db();

        var ratings = await db.Interactions.AsNoTracking()
            .Where(i => i.ContentId == contentId && i.Rating != null)
            .Select(i => (double)i.Rating!.Value)
            .ToListAsync();

        var stored = await db.Contents.AsNoTracking()
            .Where(c => c.ContentId == contentId)
            .Select(c => c.AverageRating)
            .SingleAsync();

        Assert.Equal(ratings.Average(), stored!.Value, 3);

        return stored.Value;
    }
}
