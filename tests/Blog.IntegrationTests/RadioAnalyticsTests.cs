using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Radio;
using Blog.Analytics.Infrastructure.Radio;
using Blog.Analytics.Infrastructure.Stores;
using Blog.Contracts.Analytics;
using Xunit;
using CoreRadioPlay = Blog.Analytics.Core.Radio.RadioPlay;

namespace Blog.IntegrationTests;

public class NowPlayingParsingTests
{
    private static readonly DateTime At = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CaligosEvent_IsRead()
    {
        var play = NowPlaying.Parse(
            "{\"playing\": \"public_html/music/a.mp3\", \"artist\": \"Radiohead\", \"album\": \"OK Computer\", " +
            "\"title\": \"Lucky\", \"bitrate\": 128, \"is_request\": false}", At);

        Assert.Equal(new CoreRadioPlay(At, "public_html/music/a.mp3", "Radiohead", "OK Computer", "Lucky", false), play);
    }

    [Fact]
    public void ATitleWithQuotes_WhichCaligoDoesNotEscape_IsStillRead()
    {
        var play = NowPlaying.Parse(
            "{\"playing\": \"/tmp/radio_1.mp3\", \"artist\": \"Muse\", \"album\": \"Live\", " +
            "\"title\": \"The \"Big\" One\", \"bitrate\": 128, \"is_request\": true}", At);

        Assert.NotNull(play);
        Assert.Equal("The \"Big\" One", play!.Title);
        Assert.Equal("Muse", play.Artist);
        Assert.True(play.IsRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"artist\": \"No Title\"}")]
    [InlineData("{\"title\": \"\"}")]
    public void AnythingElse_IsIgnored(string? data)
    {
        Assert.Null(NowPlaying.Parse(data, At));
    }

    [Fact]
    public void OverlongFields_AreCut()
    {
        var play = NowPlaying.Parse($"{{\"title\": \"{new string('x', 2000)}\"}}", At);

        Assert.Equal(NowPlaying.MaxFieldLength, play!.Title.Length);
    }

    [Fact]
    public void TheSameTrackSoonAfter_ContinuesThePlay_ButNotMuchLater()
    {
        var first = new CoreRadioPlay(At, "a.mp3", "Artist", "Album", "Title", false);

        Assert.True((first with { StartedAt = At.AddMinutes(5) }).ContinuesFrom(first));
        Assert.False((first with { StartedAt = At.AddHours(2) }).ContinuesFrom(first));
        Assert.False((first with { StartedAt = At.AddMinutes(5), Title = "Other" }).ContinuesFrom(first));
        Assert.False(first.ContinuesFrom(null));
    }
}

[Collection(InfrastructureCollection.Name)]
public class RadioAnalyticsTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ClickHouseRadioStore _store = null!;

    public RadioAnalyticsTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        var options = Options.Create(new ClickHouseOptions { ConnectionString = _infrastructure.ClickHouseConnectionString });

        await new ClickHouseOlapStore(options, NullLogger<ClickHouseOlapStore>.Instance).EnsureSchemaAsync();

        _store = new ClickHouseRadioStore(options);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Plays_AreReadBackNewestFirst()
    {
        var artist = Marker();
        var start = DateTime.UtcNow.AddMinutes(-10);

        await _store.WritePlayAsync(new CoreRadioPlay(start, "public_html/music/1.mp3", artist, "Album", "One", false));
        await _store.WritePlayAsync(new CoreRadioPlay(start.AddMinutes(4), "/tmp/radio_2.mp3", artist, "Album", "Two", true));

        var plays = await PlaysOfAsync(artist);

        Assert.Equal(new[] { "Two", "One" }, plays.Select(p => p.Title));
        Assert.True(plays[0].IsRequest);
        Assert.Equal(DateTimeKind.Utc, plays[0].StartedAt.Kind);
        Assert.True(Math.Abs((plays[1].StartedAt - start).TotalSeconds) < 1);
    }

    [Fact]
    public async Task ListenerSamples_AreKeptPerSource()
    {
        var source = "test-" + Marker();
        var minute = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        await _store.WriteListenersAsync(new ListenerSample(minute, source, 3));
        await _store.WriteListenersAsync(new ListenerSample(minute.AddMinutes(1), source, 5));
        await _store.WriteListenersAsync(new ListenerSample(minute.AddMinutes(1), "other-" + source, 99));

        var samples = await _store.ListenersAsync(source, minute, minute.AddHours(1));

        Assert.Equal(new[] { 3, 5 }, samples.Select(s => s.Listeners));
    }

    [Fact]
    public async Task TheCollector_RecordsEachTrackOnce_AcrossReconnectsAndRestarts()
    {
        var artist = Marker();

        await using var radio = await FakeRadio.StartAsync();

        radio.Current = Event(artist, "First");

        var options = Options.Create(new RadioOptions
        {
            EventsUrl = radio.Url + "/radio/events",
            ReconnectDelay = TimeSpan.FromMilliseconds(100)
        });

        using (var collector = new RadioCollector(options, _store, TimeProvider.System, NullLogger<RadioCollector>.Instance))
        {
            await collector.StartAsync(CancellationToken.None);

            await EventuallyAsync(async () => (await PlaysOfAsync(artist)).Count == 1);

            radio.DropAll();

            await EventuallyAsync(() => Task.FromResult(radio.Connections >= 2));
            await Task.Delay(300);

            Assert.Single(await PlaysOfAsync(artist));

            await radio.SendAsync(Event(artist, "Second", request: true));

            await EventuallyAsync(async () => (await PlaysOfAsync(artist)).Count == 2);

            await collector.StopAsync(CancellationToken.None);
        }

        using (var restarted = new RadioCollector(options, _store, TimeProvider.System, NullLogger<RadioCollector>.Instance))
        {
            var before = radio.Connections;

            await restarted.StartAsync(CancellationToken.None);

            await EventuallyAsync(() => Task.FromResult(radio.Connections > before));
            await Task.Delay(300);

            await restarted.StopAsync(CancellationToken.None);
        }

        var plays = await PlaysOfAsync(artist);

        Assert.Equal(new[] { "Second", "First" }, plays.Select(p => p.Title));
        Assert.True(plays[0].IsRequest);
        Assert.False(plays[1].IsRequest);
    }

    [Fact]
    public async Task TheCollector_SamplesTheRelaysListenerCount()
    {
        var source = "test-" + Marker();

        await using var radio = await FakeRadio.StartAsync();

        radio.Listeners = 7;

        var options = Options.Create(new RadioOptions
        {
            StatsUrl = radio.Url + "/live/stats",
            StatsSource = source,
            SampleInterval = TimeSpan.FromMilliseconds(200)
        });

        using var collector = new RadioCollector(options, _store, TimeProvider.System, NullLogger<RadioCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);

        await EventuallyAsync(async () => (await SamplesOfAsync(source)).Count >= 2);

        radio.Listeners = 9;

        await EventuallyAsync(async () => (await SamplesOfAsync(source)).Any(s => s.Listeners == 9));

        await collector.StopAsync(CancellationToken.None);

        var samples = await SamplesOfAsync(source);

        Assert.Equal(7, samples[0].Listeners);
        Assert.All(samples, sample => Assert.Contains(sample.Listeners, new[] { 7, 9 }));
    }

    [Fact]
    public async Task TheReader_ServesRecentPlays_AndSaysWhenThereIsNoStore()
    {
        var artist = Marker();

        await _store.WritePlayAsync(new CoreRadioPlay(DateTime.UtcNow, "public_html/music/x.mp3", artist, "Album", "Newest", false));

        using var withStore = new AnalyticsHostFactory(_infrastructure, new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClickHouse"] = _infrastructure.ClickHouseConnectionString
        });

        var recent = await ClientFor(withStore).GetRadioPlaysAsync(new GetRadioPlaysRequest { Limit = 5 });

        Assert.True(recent.Enabled);
        Assert.InRange(recent.Plays.Count, 1, 5);
        Assert.Contains(recent.Plays, p => p.Artist == artist && p.Title == "Newest");

        using var withoutStore = new AnalyticsHostFactory(_infrastructure);

        var none = await ClientFor(withoutStore).GetRadioPlaysAsync(new GetRadioPlaysRequest { Limit = 5 });

        Assert.False(none.Enabled);
        Assert.Empty(none.Plays);
    }

    [Fact]
    public async Task TheBlogApi_ServesRadioHistory_FromTheAnalyticsService()
    {
        var artist = Marker();

        await _store.WritePlayAsync(
            new CoreRadioPlay(DateTime.UtcNow, "/tmp/radio_9.mp3", artist, "Album", "Through The Api", true));

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

        var history = await wired.CreateClient().GetFromJsonAsync<JsonElement>("/api/radio/recent?limit=50");

        Assert.True(history.GetProperty("enabled").GetBoolean());

        var play = history.GetProperty("plays").EnumerateArray()
            .Single(p => p.GetProperty("artist").GetString() == artist);

        Assert.Equal("Through The Api", play.GetProperty("title").GetString());
        Assert.True(play.GetProperty("isRequest").GetBoolean());
        Assert.Equal(JsonValueKind.Null, play.GetProperty("contentId").ValueKind);
        Assert.False(play.TryGetProperty("file", out _));
    }

    private static AnalyticsReader.AnalyticsReaderClient ClientFor(AnalyticsHostFactory host) =>
        new(GrpcChannel.ForAddress(
            host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() }));

    private async Task<List<CoreRadioPlay>> PlaysOfAsync(string artist) =>
        (await _store.RecentPlaysAsync(5000)).Where(p => p.Artist == artist).ToList();

    private Task<IReadOnlyList<ListenerSample>> SamplesOfAsync(string source) =>
        _store.ListenersAsync(source, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

    private static string Marker() => "artist-" + Guid.NewGuid().ToString("N");

    private static string Event(string artist, string title, bool request = false) =>
        $"{{\"playing\": \"public_html/music/{title}.mp3\", \"artist\": \"{artist}\", \"album\": \"Album\", " +
        $"\"title\": \"{title}\", \"bitrate\": 128, \"is_request\": {(request ? "true" : "false")}}}";

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The condition was not met within 20 seconds.");
    }

    private sealed class FakeRadio : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<HttpContext> _listeners = new();
        private readonly SemaphoreSlim _writing = new(1, 1);
        private int _connections;

        private FakeRadio(WebApplication app)
        {
            _app = app;
        }

        public string Url { get; private set; } = string.Empty;

        public string? Current { get; set; }

        public int Listeners { get; set; }

        public int Connections => Volatile.Read(ref _connections);

        public static async Task<FakeRadio> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var app = builder.Build();
            var radio = new FakeRadio(app);

            app.MapGet("/radio/events", radio.ServeAsync);
            app.MapGet("/live/stats", () => Results.Json(new { listeners = radio.Listeners, uptime_s = 1 }));

            await app.StartAsync();

            radio.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            return radio;
        }

        public async Task SendAsync(string data)
        {
            Current = data;

            List<HttpContext> listeners;

            lock (_listeners)
            {
                listeners = _listeners.ToList();
            }

            foreach (var listener in listeners)
            {
                await WriteAsync(listener, data);
            }
        }

        public void DropAll()
        {
            List<HttpContext> listeners;

            lock (_listeners)
            {
                listeners = _listeners.ToList();
            }

            foreach (var listener in listeners)
            {
                listener.Abort();
            }
        }

        public async ValueTask DisposeAsync()
        {
            DropAll();

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private async Task ServeAsync(HttpContext context)
        {
            context.Response.ContentType = "text/event-stream";

            await context.Response.Body.FlushAsync();

            lock (_listeners)
            {
                _listeners.Add(context);
            }

            Interlocked.Increment(ref _connections);

            if (Current is { } welcome)
            {
                await WriteAsync(context, welcome);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_listeners)
            {
                _listeners.Remove(context);
            }
        }

        private async Task WriteAsync(HttpContext context, string data)
        {
            await _writing.WaitAsync();

            try
            {
                await context.Response.WriteAsync($"data: {data}\n\n");
                await context.Response.Body.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                _writing.Release();
            }
        }
    }
}
