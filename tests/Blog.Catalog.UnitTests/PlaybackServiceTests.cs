using Microsoft.Extensions.Time.Testing;
using Moq;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Media;
using Blog.Catalog.Domain.Media;
using Blog.Catalog.Domain.Users;
using Blog.SharedKernel;
using Xunit;

namespace Blog.Catalog.UnitTests;

public class PlaybackServiceTests
{
    private const string Device = "device-0001";
    private const string Storage = "3/20261014";

    private static readonly Caller Viewer = new("identity-1", false);
    private static readonly DateTime Noon = new(2026, 10, 14, 12, 0, 0);

    private readonly Mock<IStreamingAccounts> _accounts = new();
    private readonly Mock<IMediaStore> _media = new();
    private readonly Mock<IPlaybackStore> _playback = new();
    private readonly Mock<IPlaybackEventSink> _events = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Noon, TimeSpan.Zero));

    private readonly MediaRules _rules = new()
    {
        Enabled = true,
        SigningKey = "unit-test-media-signing-key-0123456789"
    };

    private readonly StreamingUser _user = new()
    {
        UserId = 21,
        SubscriptionPlanId = 5,
        SubscriptionStartDate = new DateOnly(2026, 10, 1),
        SubscriptionEndDate = new DateOnly(2026, 10, 31)
    };

    public PlaybackServiceTests()
    {
        _accounts.Setup(a => a.FindByIdentityAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync(_user);
        _accounts.Setup(a => a.FindUserIdAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync(21);
        _accounts.Setup(a => a.FindPlanAsync(5, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SubscriptionPlan
                 {
                     SubscriptionPlanId = 5,
                     PlanInfo = "{\"name\": \"Standard\", \"devices_household_can_watch_at_same_time\": 2}"
                 });

        _media.Setup(m => m.FindAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new ContentMedia
        {
            ContentId = 3,
            Status = MediaStatus.Ready,
            StoragePath = Storage,
            HlsPath = "master.m3u8",
            DashPath = "manifest.mpd",
            DurationSeconds = 600f
        });
        _media.Setup(m => m.GetRenditionsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Rendition>
        {
            new() { Kind = RenditionKinds.Video, Codec = "avc1.64001f", Bitrate = 2_800_000, Width = 1280, Height = 720, Path = "video-0.m3u8" },
            new() { Kind = RenditionKinds.Audio, Codec = "mp4a.40.2", Bitrate = 128_000, Path = "audio-1.m3u8" },
            new() { Kind = RenditionKinds.Progressive, Codec = "avc1.64001e,mp4a.40.2", Bitrate = 1_500_000, Width = 854, Height = 480, Path = "progressive.mp4" }
        });
        _media.Setup(m => m.GetSubtitlesAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SubtitleTrack>
        {
            new(7, "English", "subs/2.vtt", "vtt"),
            new(8, "Klingon", "subs/99.vtt", "vtt")
        });
    }

    private PlaybackService Service() =>
        new(_accounts.Object, _media.Object, _playback.Object, _events.Object, _rules, _time);

    private PlaybackSession Session(Guid? id = null) => new()
    {
        SessionId = id ?? Guid.NewGuid(),
        UserId = 21,
        ContentId = 3,
        DeviceId = Device,
        StartedAt = Noon,
        ExpiresAt = Noon.AddSeconds(60)
    };

    private void Opens(PlaybackSession? session, bool resumed = false, int active = 1) =>
        _playback.Setup(p => p.OpenAsync(It.IsAny<OpenSession>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OpenedSession(session, resumed, active));

    [Fact]
    public async Task Starting_ChecksThePlanAndHandsOutSignedUrlsForOneSession()
    {
        var session = Session();

        Opens(session);

        var result = await Service().StartAsync(new StartSessionCommand(3, Device), Viewer);

        Assert.True(result.IsSuccess, result.Error);

        var dto = result.Value!;

        Assert.Equal(session.SessionId, dto.SessionId);
        Assert.Equal(60, dto.LeaseSeconds);
        Assert.Equal(20, dto.HeartbeatSeconds);
        Assert.Equal(2, dto.MaxStreams);
        Assert.Equal(600f, dto.DurationSeconds);
        Assert.Equal(0f, dto.Position);
        Assert.Equal(new DateTimeOffset(Noon.AddSeconds(60), TimeSpan.Zero), dto.ExpiresAt);

        var token = dto.PlaylistUrl!.Split('/')[2];

        Assert.Equal($"/vod/{token}/master.m3u8", dto.PlaylistUrl);
        Assert.Equal($"/vod/{token}/manifest.mpd", dto.ManifestUrl);
        Assert.Equal($"/vod/{token}/progressive.mp4", dto.ProgressiveUrl);
        Assert.Equal($"/vod/{token}/subs/2.vtt", dto.Subtitles[0].Url);
        Assert.Equal("en", dto.Subtitles[0].Language);
        Assert.Null(dto.Subtitles[1].Language);
        Assert.Equal(new[] { RenditionKinds.Video, RenditionKinds.Audio }, dto.Renditions.Select(r => r.Kind));

        Assert.True(MediaToken.TryRead(System.Text.Encoding.UTF8.GetBytes(_rules.SigningKey), token, out var grant));
        Assert.Equal(session.SessionId, grant.SessionId);
        Assert.Equal(new DateTimeOffset(Noon, TimeSpan.Zero) + _rules.MaxSessionAge, grant.ExpiresAt);

        _playback.Verify(p => p.OpenAsync(
            It.Is<OpenSession>(o =>
                o.UserId == 21 && o.ContentId == 3 && o.DeviceId == Device && o.MaxStreams == 2 &&
                o.Now == Noon && o.Now.Kind == DateTimeKind.Unspecified && o.ExpiresAt == Noon.AddSeconds(60) &&
                o.PurgeBefore == Noon - _rules.SessionRetention),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Starting_ResumesWhereTheViewerStopped_UnlessTheyWereAtTheEnd()
    {
        Opens(Session());

        _playback.Setup(p => p.GetPositionAsync(21, 3, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new PlaybackPosition { UserId = 21, ContentId = 3, Position = 245.5f, Seq = 9 });

        Assert.Equal(245.5f, (await Service().StartAsync(new StartSessionCommand(3, Device), Viewer)).Value!.Position);

        _playback.Setup(p => p.GetPositionAsync(21, 3, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new PlaybackPosition { UserId = 21, ContentId = 3, Position = 590f, Seq = 40 });

        Assert.Equal(0f, (await Service().StartAsync(new StartSessionCommand(3, Device), Viewer)).Value!.Position);
    }

    [Fact]
    public async Task APlanWithoutAStreamCount_FallsBackToTheConfiguredLimit()
    {
        _accounts.Setup(a => a.FindPlanAsync(5, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SubscriptionPlan { SubscriptionPlanId = 5, PlanInfo = "free text" });
        _rules.DefaultMaxStreams = 3;

        Opens(Session());

        await Service().StartAsync(new StartSessionCommand(3, Device), Viewer);

        _playback.Verify(p => p.OpenAsync(It.Is<OpenSession>(o => o.MaxStreams == 3), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task WhenEveryStreamIsTaken_StartingIsAConflictThatSaysWhy()
    {
        Opens(null, active: 2);

        var result = await Service().StartAsync(new StartSessionCommand(3, Device), Viewer);

        Assert.Equal(ResultStatus.Conflict, result.Status);
        Assert.Equal(PlaybackService.LimitReached(2, 2), result.Error);
        Assert.Contains("2 streams", result.Error);
    }

    [Fact]
    public async Task Starting_NeedsAnAccountAProfileAnActiveSubscriptionAndAVideo()
    {
        var service = Service();

        Assert.Equal(ResultStatus.Unauthorized, (await service.StartAsync(new StartSessionCommand(3, Device), Caller.Anonymous)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.StartAsync(new StartSessionCommand(3, "x"), Viewer)).Status);
        Assert.Equal(ResultStatus.NotFound, (await service.StartAsync(new StartSessionCommand(4, Device), Viewer)).Status);

        _user.SubscriptionEndDate = new DateOnly(2026, 10, 13);

        var lapsed = await service.StartAsync(new StartSessionCommand(3, Device), Viewer);

        Assert.Equal(ResultStatus.Forbidden, lapsed.Status);
        Assert.Equal(PlaybackService.SubscriptionInactive, lapsed.Error);

        _accounts.Setup(a => a.FindByIdentityAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync((StreamingUser?)null);

        var stranger = await service.StartAsync(new StartSessionCommand(3, Device), Viewer);

        Assert.Equal(ResultStatus.Conflict, stranger.Status);
        Assert.Equal(StreamingAccountService.ProfileRequired, stranger.Error);

        _playback.Verify(p => p.OpenAsync(It.IsAny<OpenSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ATitleWhoseFirstPackagingHasNotFinished_CannotBeStarted()
    {
        _media.Setup(m => m.FindAsync(3, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ContentMedia { ContentId = 3, Status = MediaStatus.Packaging });

        Assert.Equal(ResultStatus.NotFound, (await Service().StartAsync(new StartSessionCommand(3, Device), Viewer)).Status);
    }

    [Fact]
    public async Task APositionReport_RenewsTheLease()
    {
        var session = Session();

        _playback.Setup(p => p.FindAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _playback.Setup(p => p.SavePositionAsync(session, 42f, 7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((PlaybackSession _, float position, long seq, DateTime _, DateTime expires, CancellationToken _) =>
                     new SavedPosition(position, seq, expires));

        _time.Advance(TimeSpan.FromSeconds(30));

        var result = await Service().ReportPositionAsync(session.SessionId, new PositionCommand(42f, 7), Viewer);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(42f, result.Value!.Position);
        Assert.Equal(7, result.Value.Seq);
        Assert.Equal(60, result.Value.LeaseSeconds);
        Assert.Equal(new DateTimeOffset(Noon.AddSeconds(90), TimeSpan.Zero), result.Value.ExpiresAt);
    }

    [Fact]
    public async Task TheLease_NeverOutlivesTheSignedUrl()
    {
        var session = Session();

        session.ExpiresAt = Noon + _rules.MaxSessionAge;

        _playback.Setup(p => p.FindAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _playback.Setup(p => p.SavePositionAsync(session, 1f, 1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((PlaybackSession _, float position, long seq, DateTime _, DateTime expires, CancellationToken _) =>
                     new SavedPosition(position, seq, expires));

        _time.Advance(_rules.MaxSessionAge - TimeSpan.FromSeconds(10));

        var late = await Service().ReportPositionAsync(session.SessionId, new PositionCommand(1f, 1), Viewer);

        Assert.Equal(10, late.Value!.LeaseSeconds);

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(ResultStatus.Gone, (await Service().ReportPositionAsync(session.SessionId, new PositionCommand(1f, 1), Viewer)).Status);
    }

    [Fact]
    public async Task APositionReport_ForALapsedEndedOrForeignSession_IsRefused()
    {
        var lapsed = Session();
        var ended = Session();
        var foreign = Session();

        ended.EndedAt = Noon;
        foreign.UserId = 99;

        _playback.Setup(p => p.FindAsync(lapsed.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(lapsed);
        _playback.Setup(p => p.FindAsync(ended.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(ended);
        _playback.Setup(p => p.FindAsync(foreign.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);

        var service = Service();

        Assert.Equal(ResultStatus.Gone, (await service.ReportPositionAsync(ended.SessionId, new PositionCommand(1f, 1), Viewer)).Status);
        Assert.Equal(ResultStatus.NotFound, (await service.ReportPositionAsync(foreign.SessionId, new PositionCommand(1f, 1), Viewer)).Status);
        Assert.Equal(ResultStatus.NotFound, (await service.ReportPositionAsync(Guid.NewGuid(), new PositionCommand(1f, 1), Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.ReportPositionAsync(lapsed.SessionId, new PositionCommand(-1f, 1), Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.ReportPositionAsync(lapsed.SessionId, new PositionCommand(1f, -1), Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.ReportPositionAsync(lapsed.SessionId, new PositionCommand(float.NaN, 1), Viewer)).Status);

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(ResultStatus.Gone, (await service.ReportPositionAsync(lapsed.SessionId, new PositionCommand(1f, 1), Viewer)).Status);

        _playback.Verify(
            p => p.SavePositionAsync(It.IsAny<PlaybackSession>(), It.IsAny<float>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Completing_ReportsWhetherItWasTheFirstTime()
    {
        var session = Session();
        var completionId = Guid.NewGuid();

        _playback.Setup(p => p.FindAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var stored = Session(session.SessionId);

        stored.CompletionId = completionId;
        stored.CompletedAt = Noon;
        stored.WatchedContentId = 910;

        _playback.SetupSequence(p => p.CompleteAsync(session.SessionId, 21, completionId, 599f, Noon, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Completion(CompletionOutcome.Completed, stored))
                 .ReturnsAsync(new Completion(CompletionOutcome.Repeated, stored));

        var first = await Service().CompleteAsync(session.SessionId, new CompleteCommand(completionId, 599f), Viewer);
        var second = await Service().CompleteAsync(session.SessionId, new CompleteCommand(completionId, 599f), Viewer);

        Assert.False(first.Value!.Repeated);
        Assert.True(second.Value!.Repeated);
        Assert.Equal(910, first.Value.WatchedContentId);
        Assert.Equal(first.Value.WatchedContentId, second.Value.WatchedContentId);
        Assert.Equal(first.Value.CompletedAt, second.Value.CompletedAt);
    }

    [Fact]
    public async Task Completing_WithAnotherIdOrWithoutOne_IsRefused()
    {
        var session = Session();

        _playback.Setup(p => p.FindAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _playback.Setup(p => p.CompleteAsync(session.SessionId, 21, It.IsAny<Guid>(), null, Noon, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Completion(CompletionOutcome.Mismatch, session));

        var service = Service();

        Assert.Equal(ResultStatus.Conflict, (await service.CompleteAsync(session.SessionId, new CompleteCommand(Guid.NewGuid(), null), Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.CompleteAsync(session.SessionId, new CompleteCommand(Guid.Empty, null), Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await service.CompleteAsync(session.SessionId, new CompleteCommand(Guid.NewGuid(), -3f), Viewer)).Status);
        Assert.Equal(ResultStatus.NotFound, (await service.CompleteAsync(Guid.NewGuid(), new CompleteCommand(Guid.NewGuid(), null), Viewer)).Status);
        Assert.Equal(ResultStatus.Unauthorized, (await service.CompleteAsync(session.SessionId, new CompleteCommand(Guid.NewGuid(), null), Caller.Anonymous)).Status);
    }

    [Fact]
    public async Task Events_AreStampedWithTheSessionsOwnDevice_AndBadOnesAreDropped()
    {
        var session = Session();
        IReadOnlyList<PlaybackEventRecord>? published = null;

        _playback.Setup(p => p.FindAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _events.Setup(e => e.Publish(It.IsAny<IReadOnlyList<PlaybackEventRecord>>()))
               .Callback((IReadOnlyList<PlaybackEventRecord> records) => published = records);

        var at = new DateTimeOffset(Noon.AddSeconds(-5), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var startup = Guid.NewGuid();

        var result = await Service().RecordEventsAsync(
            session.SessionId,
            new List<PlaybackEventCommand>
            {
                new(startup, 1, PlaybackEventTypes.Startup, at, 0f, 740, null, null, PlaybackDeliveries.Hls, null),
                new(Guid.NewGuid(), 2, PlaybackEventTypes.Quality, null, 12f, null, 360, 99_999, "smoke-signals", new string('x', 500)),
                new(Guid.NewGuid(), 3, "explode", at, 0f, null, null, null, null, null),
                new(Guid.Empty, 4, PlaybackEventTypes.Stall, at, 0f, 100, null, null, null, null),
                new(Guid.NewGuid(), -1, PlaybackEventTypes.Stall, at, 0f, 100, null, null, null, null),
                new(Guid.NewGuid(), 5, PlaybackEventTypes.Stall, 1, float.NaN, -50, null, null, PlaybackDeliveries.Dash, " late ")
            },
            Viewer);

        Assert.Equal(new EventsAcceptedDto(3, 3), result.Value);
        Assert.NotNull(published);
        Assert.All(published!, record =>
        {
            Assert.Equal(Device, record.DeviceId);
            Assert.Equal(session.SessionId, record.SessionId);
            Assert.Equal(3, record.ContentId);
        });

        Assert.Equal(startup, published![0].EventId);
        Assert.Equal(Noon.AddSeconds(-5), published[0].OccurredAt);
        Assert.Equal(740, published[0].DurationMs);

        Assert.Equal(string.Empty, published[1].Delivery);
        Assert.Equal(PlaybackService.MaxHeight, published[1].ToHeight);
        Assert.Equal(PlaybackService.MaxDetailLength, published[1].Detail.Length);
        Assert.Equal(Noon, published[1].OccurredAt);

        Assert.Equal(Noon, published[2].OccurredAt);
        Assert.Equal(0f, published[2].Position);
        Assert.Equal(0, published[2].DurationMs);
        Assert.Equal("late", published[2].Detail);
    }

    [Fact]
    public async Task ABatchThatIsTooLarge_IsRefusedWhole()
    {
        var events = Enumerable.Range(0, MediaRules.MaxEventsPerBatch + 1)
            .Select(i => new PlaybackEventCommand(Guid.NewGuid(), i, PlaybackEventTypes.Play, null, null, null, null, null, null, null))
            .ToList();

        Assert.Equal(ResultStatus.Invalid, (await Service().RecordEventsAsync(Guid.NewGuid(), events, Viewer)).Status);
        Assert.Equal(ResultStatus.Invalid, (await Service().RecordEventsAsync(Guid.NewGuid(), null, Viewer)).Status);

        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheVodServer_LearnsHowLongASessionLivesAndWhereItsPackageIs()
    {
        var live = Guid.NewGuid();

        _playback.Setup(p => p.GetAccessAsync(live, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new MediaAccess(Noon.AddSeconds(60), null, Storage));

        var access = await Service().GetAccessAsync(live);

        Assert.True(access.IsSuccess);
        Assert.Equal(new SessionAccessDto(live, new DateTimeOffset(Noon.AddSeconds(60), TimeSpan.Zero).ToUnixTimeSeconds(), Storage), access.Value);

        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(ResultStatus.Gone, (await Service().GetAccessAsync(live)).Status);
    }

    [Fact]
    public async Task AnEndedOrUnpackagedSession_IsGone_AndAnUnknownOneIsNotFound()
    {
        var ended = Guid.NewGuid();
        var unpackaged = Guid.NewGuid();

        _playback.Setup(p => p.GetAccessAsync(ended, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new MediaAccess(Noon.AddSeconds(60), Noon, Storage));
        _playback.Setup(p => p.GetAccessAsync(unpackaged, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new MediaAccess(Noon.AddSeconds(60), null, null));

        Assert.Equal(ResultStatus.Gone, (await Service().GetAccessAsync(ended)).Status);
        Assert.Equal(ResultStatus.Gone, (await Service().GetAccessAsync(unpackaged)).Status);
        Assert.Equal(ResultStatus.NotFound, (await Service().GetAccessAsync(Guid.NewGuid())).Status);
    }

    [Fact]
    public async Task Ending_IsANoOpForAnEndedSession_AndUnknownForAStranger()
    {
        var known = Guid.NewGuid();

        _playback.Setup(p => p.EndAsync(known, 21, Noon, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Assert.True((await Service().EndAsync(known, Viewer)).IsSuccess);
        Assert.True((await Service().EndAsync(known, Viewer)).IsSuccess);
        Assert.Equal(ResultStatus.NotFound, (await Service().EndAsync(Guid.NewGuid(), Viewer)).Status);
        Assert.Equal(ResultStatus.Unauthorized, (await Service().EndAsync(known, Caller.Anonymous)).Status);
    }
}
