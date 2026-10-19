using System.Text;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Contents;
using Blog.Catalog.Application.Media;
using Blog.Catalog.Domain.Media;
using Blog.SharedKernel;
using Xunit;

namespace Blog.Catalog.UnitTests;

public class PackagingServiceTests
{
    public const string ReferenceKey = "caligo-test-signing-key-0123456789abcdef";
    public const string ReferenceSession = "3f2a9c1e-7b1d-4a0e-9b7a-1c2d3e4f5a6b";
    public const long ReferenceExpiry = 1760443200;
    public const string ReferenceStorage = "3/20261014120000-ab12cd34";
    public const string ReferenceToken = "PyqcHnsdSg6behwtPk9aawAAAABo7jtAjOEbrISsGZBuanx2TvJ_zo0XjfZo9DD1";
    public const string ReferenceInternalKey = "f6e9386e449212e98b8f06c6666b2f19a23c6992a96d80ef0246aede2d58547e";

    private static readonly DateTime Noon = new(2026, 10, 14, 12, 0, 0);

    private readonly Mock<IPackagingQueue> _queue = new();
    private readonly Mock<IMediaInbox> _inbox = new();
    private readonly Mock<ICatalogReader> _catalog = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Noon, TimeSpan.Zero));
    private readonly MediaRules _rules = new() { Enabled = true, SigningKey = ReferenceKey };

    private PackagingService Service() => new(_queue.Object, _inbox.Object, _catalog.Object, _rules, _time);

    [Fact]
    public void TheTokenFormat_MatchesTheReferenceTheCServerIsTestedAgainst()
    {
        var token = MediaToken.Create(
            Encoding.UTF8.GetBytes(ReferenceKey),
            Guid.Parse(ReferenceSession),
            DateTimeOffset.FromUnixTimeSeconds(ReferenceExpiry),
            ReferenceStorage);

        Assert.Equal(ReferenceToken, token);
        Assert.Equal(0x8ce11bac84ac1990UL, MediaToken.ScopeOf(ReferenceStorage));
    }

    [Fact]
    public void TheInternalKey_IsDerivedFromTheSigningKey_AndComparedWholly()
    {
        Assert.Equal(ReferenceInternalKey, InternalKeys.Derive(ReferenceKey));
        Assert.True(InternalKeys.Matches(ReferenceKey, ReferenceInternalKey));
        Assert.False(InternalKeys.Matches(ReferenceKey, ReferenceInternalKey[..^1]));
        Assert.False(InternalKeys.Matches(ReferenceKey, ReferenceInternalKey.ToUpperInvariant()));
        Assert.False(InternalKeys.Matches(ReferenceKey, null));
        Assert.False(InternalKeys.Matches(string.Empty, InternalKeys.Derive(string.Empty)));
    }

    [Fact]
    public async Task AClaim_CarriesTheSidecarSubtitlesWhoseLanguageTheCatalogKnows()
    {
        _queue.Setup(q => q.ClaimAsync(Noon, Noon.AddMinutes(2), 3, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PackagingJob(3, "Fight Club.mp4", 1, "3/old"));
        _inbox.SetupGet(i => i.IsConfigured).Returns(true);
        _inbox.Setup(i => i.List()).Returns(new List<InboxEntry>
        {
            new("Fight Club.mp4", 10, Noon),
            new("Fight Club.English.vtt", 1, Noon),
            new("Fight Club.english.srt", 1, Noon),
            new("Fight Club.Klingon.vtt", 1, Noon),
            new("Fight Club.Spanish.srt", 1, Noon)
        });
        _catalog.Setup(c => c.GetLanguagesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NamedDto> { new(1, "Spanish"), new(2, "English") });

        var job = await Service().ClaimAsync();

        Assert.NotNull(job);
        Assert.Equal(3, job!.ContentId);
        Assert.Equal("3/old", job.StoragePath);
        Assert.Equal(120, job.ClaimSeconds);
        Assert.Equal(
            new[] { ("Fight Club.English.vtt", 2, "en"), ("Fight Club.Spanish.srt", 1, "es") },
            job.Subtitles.Select(s => (s.File, s.LanguageId, s.Tag!)));
    }

    [Fact]
    public async Task NothingQueued_MeansNoJob()
    {
        Assert.Null(await Service().ClaimAsync());
    }

    [Fact]
    public async Task ALostClaim_IsAConflictForEveryStep()
    {
        var claim = new PackagingClaimRef(1, "clip.mp4");
        var service = Service();

        Assert.Equal(ResultStatus.Conflict, (await service.ExtendAsync(3, claim)).Status);
        Assert.Equal(ResultStatus.Conflict, (await service.FailAsync(3, claim, "boom")).Status);
        Assert.Equal(ResultStatus.Conflict, (await service.ReleaseAsync(3, claim)).Status);
        Assert.Equal(ResultStatus.Conflict, (await service.CompleteAsync(3, Report())).Status);
    }

    [Fact]
    public async Task AFinishedPackage_IsPublishedWithItsRenditionsAndSubtitles()
    {
        PackagingResult? published = null;

        _queue.Setup(q => q.PublishAsync(
                  It.Is<PackagingJob>(j => j.ContentId == 3 && j.Attempts == 1 && j.SourceFile == "clip.mp4"),
                  It.IsAny<PackagingResult>(), Noon, It.IsAny<CancellationToken>()))
              .Callback((PackagingJob _, PackagingResult result, DateTime _, CancellationToken _) => published = result)
              .ReturnsAsync(true);

        var result = await Service().CompleteAsync(3, Report());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("3/20261014120000-ab12cd34", published!.StoragePath);
        Assert.Equal(3, published.Renditions.Count);
        Assert.Single(published.Subtitles);
    }

    [Fact]
    public void AReport_IsCheckedBeforeItReachesTheDatabase()
    {
        Assert.Null(PackagingService.Validate(3, Report()));

        Assert.NotNull(PackagingService.Validate(3, Report() with { StoragePath = "4/20261014" }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { StoragePath = "3/../4" }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { HlsPath = null, DashPath = null }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { HlsPath = "master.mpd" }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { DashPath = "../manifest.mpd" }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { DurationSeconds = 0 }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { DurationSeconds = float.NaN }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new List<PackagedRendition>() }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new[] { Video() with { Kind = "hologram" } } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new[] { Video() with { Codec = "avc1\"><x" } } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new[] { Video() with { Bitrate = 0 } } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new[] { Video() with { Height = 0 } } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Renditions = new[] { Video() with { Path = "video-0.txt" } } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Subtitles = new[] { new PackagedSubtitle(0, "subs/0.vtt") } }));
        Assert.NotNull(PackagingService.Validate(3, Report() with { Subtitles = new[] { new PackagedSubtitle(2, "subs/2.srt") } }));
    }

    private static PackagedRendition Video() =>
        new(RenditionKinds.Video, "avc1.64001f", 2_900_000, 1280, 720, null, "video-0.m3u8");

    private static PackagingReport Report() => new(
        1,
        "clip.mp4",
        "3/20261014120000-ab12cd34",
        "master.m3u8",
        "manifest.mpd",
        22f,
        new[]
        {
            Video(),
            new PackagedRendition(RenditionKinds.Audio, "mp4a.40.2", 130_000, null, null, "und", "audio-3.m3u8"),
            new PackagedRendition(RenditionKinds.Progressive, "avc1.64001e,mp4a.40.2", 1_500_000, 854, 480, null, "progressive.mp4")
        },
        new[] { new PackagedSubtitle(2, "subs/2.vtt") });
}
