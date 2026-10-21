using Microsoft.Extensions.Time.Testing;
using Moq;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Collections;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Application.Engine;
using Caligo.SharedKernel;
using Xunit;

namespace Caligo.Catalog.UnitTests;

public class ThesaurariumServiceTests
{
    private static readonly Caller Reader = new("identity-1", false);
    private static readonly Caller Admin = new("identity-admin", true);

    private readonly Mock<ICollectionStore> _store = new();
    private readonly Mock<ICatalogViewer> _viewer = new();
    private readonly Mock<ICaligoEngine> _engine = new();
    private readonly Mock<IMixtapeLookup> _mixtape = new();
    private readonly Mock<IMusicSync> _sync = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    public ThesaurariumServiceTests()
    {
        _viewer.Setup(v => v.HiddenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HiddenMusic.None);
        _viewer.Setup(v => v.UserName).Returns("ashot");
        _engine.Setup(e => e.IsConfigured).Returns(true);
        _store.Setup(s => s.OwnsAsync(It.IsAny<int>(), Reader.UserId!, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _store.Setup(s => s.MissingContentAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<int>());
    }

    private CollectionService Collections() => new(_store.Object, _viewer.Object, _time);

    private CaligoService Caligo() => new(_engine.Object, _viewer.Object, _mixtape.Object, _sync.Object);

    private static HiddenMusic Hiding(params int[] contentIds) =>
        new(contentIds, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

    [Fact]
    public async Task Guests_HaveNoCollections()
    {
        var result = await Collections().ListAsync(Caller.Anonymous);

        Assert.Equal(ResultStatus.Unauthorized, result.Status);
        _store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ACollection_NeedsAName(string name)
    {
        var result = await Collections().CreateAsync(new CollectionNameCommand(name), Reader);

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ACollectionName_HasAnUpperLimit()
    {
        var result = await Collections().CreateAsync(new CollectionNameCommand(new string('x', 101)), Reader);

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task TheSameNameTwice_IsAConflict()
    {
        _store.Setup(s => s.NameTakenAsync(Reader.UserId!, "Late Night", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Collections().CreateAsync(new CollectionNameCommand("Late Night"), Reader);

        Assert.Equal(ResultStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task ANewCollection_IsCreatedUnderItsTrimmedName()
    {
        _store.Setup(s => s.CreateAsync(Reader.UserId!, "Late Night", It.Is<DateTime>(at => at.Kind == DateTimeKind.Unspecified), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new CollectionSummaryDto(7, "Late Night", 0, 0, null));

        var result = await Collections().CreateAsync(new CollectionNameCommand("  Late Night "), Reader);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.CollectionId);
    }

    [Fact]
    public async Task SomeoneElsesCollection_LooksLikeItDoesNotExist()
    {
        _store.Setup(s => s.OwnsAsync(5, Reader.UserId!, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Collections().AddAsync(5, new AddTitlesCommand(new[] { 1 }), Reader);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        _store.Verify(s => s.AddAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APrivateTitleOfSomeoneElse_CannotBeAdded()
    {
        _viewer.Setup(v => v.HiddenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Hiding(42));

        var result = await Collections().AddAsync(5, new AddTitlesCommand(new[] { 41, 42 }), Reader);

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task AMissingTitle_CannotBeAdded()
    {
        _store.Setup(s => s.MissingContentAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { 99 });

        var result = await Collections().AddAsync(5, new AddTitlesCommand(new[] { 99 }), Reader);

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task TooManyTitlesAtOnce_AreRejected()
    {
        var ids = Enumerable.Range(1, CollectionService.MaxTitlesPerAdd + 1).ToArray();

        var result = await Collections().AddAsync(5, new AddTitlesCommand(ids), Reader);

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task RepeatedTitles_AreAddedOnce()
    {
        _store.Setup(s => s.AddAsync(5, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 3, 4 })), It.Is<DateTime>(at => at.Kind == DateTimeKind.Unspecified), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AddTitlesResult(2, 0));

        var result = await Collections().AddAsync(5, new AddTitlesCommand(new[] { 3, 4, 3 }), Reader);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Added);
    }

    [Fact]
    public void Guests_CannotIngest()
    {
        var result = Caligo().Ingest("Radiohead OK Computer", Caller.Anonymous);

        Assert.Equal(ResultStatus.Unauthorized, result.Status);
        Assert.Equal("ACCESS DENIED: LOGIN REQUIRED", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyIngest_IsRejected(string query)
    {
        var result = Caligo().Ingest(query, Reader);

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Ingest_StreamsCaligosLinesUnderThePlatformName_AndSyncsAfterwards()
    {
        _engine.Setup(e => e.RequestSongAsync("ashot", "OK Computer", It.IsAny<CancellationToken>()))
               .Returns(Lines("> Queueing 12 tracks...", "SUCCESS. Album ready at: ok-computer"));

        var result = Caligo().Ingest(" OK Computer ", Reader);
        var lines = new List<string>();

        await foreach (var line in result.Value!)
        {
            lines.Add(line);
            _sync.Verify(s => s.Request(), Times.Never);
        }

        Assert.Equal(2, lines.Count);
        _sync.Verify(s => s.Request(), Times.Once);
    }

    [Fact]
    public async Task SignedInListeners_KeepTheirAiMemoryOnTheirAccount()
    {
        _engine.Setup(e => e.AskAsync("user-identity-1", "who sang Creep", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AskAnswer("Radiohead", Array.Empty<AskSong>()));

        var result = await Caligo().AskAsync(new AskCommand("who sang Creep", "sess_1_abc"), Reader);

        Assert.Equal("Radiohead", result.Value!.Text);
    }

    [Fact]
    public async Task Guests_UseTheirBrowsersAiSession()
    {
        _engine.Setup(e => e.AskAsync("sess_1729_abc123", "hi", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AskAnswer("hello", Array.Empty<AskSong>()));

        var result = await Caligo().AskAsync(new AskCommand("hi", "sess_1729_abc123"), Caller.Anonymous);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has spaces")]
    [InlineData("$(rm -rf /)")]
    public async Task AGuestWithoutAUsableSession_IsAskedForOne(string? session)
    {
        var result = await Caligo().AskAsync(new AskCommand("hi", session), Caller.Anonymous);

        Assert.Equal(ResultStatus.Invalid, result.Status);
        _engine.Verify(e => e.AskAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASilentAi_IsReportedAsAFailedConnection()
    {
        _engine.Setup(e => e.AskAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((AskAnswer?)null);

        var result = await Caligo().AskAsync(new AskCommand("hi", null), Reader);

        Assert.Equal(ResultStatus.Unavailable, result.Status);
        Assert.Equal("Connection Failed", result.Error);
    }

    [Fact]
    public async Task OnlyMixtapeSongs_GoToTheLiveRadio()
    {
        _mixtape.Setup(m => m.TrackAsync(8, It.IsAny<CancellationToken>())).ReturnsAsync((MixtapeTrack?)null);

        var result = await Caligo().QueueAsync(new QueueTrackCommand(8), Caller.Anonymous);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        _engine.Verify(e => e.QueueToRadioAsync(It.IsAny<RadioQueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueToLiveRadio_SendsCaligoThePathAndTheCatalogsNames()
    {
        _mixtape.Setup(m => m.TrackAsync(8, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MixtapeTrack("arctic-monkeys-am", "arabella.mp3", "Arabella", "Arctic Monkeys", "AM"));
        _engine.Setup(e => e.QueueToRadioAsync(new RadioQueueRequest("arctic-monkeys-am/arabella.mp3", "Arctic Monkeys", "AM", "Arabella"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new RadioQueueResult(true, "Queued successfully", "/tmp/radio_1.mp3"));

        var result = await Caligo().QueueAsync(new QueueTrackCommand(8), Caller.Anonymous);

        Assert.Equal("/tmp/radio_1.mp3", result.Value!.TrackId);
    }

    [Fact]
    public async Task APrivateSongOfSomeoneElse_StaysOffTheLiveRadio()
    {
        _viewer.Setup(v => v.HiddenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Hiding(8));
        _mixtape.Setup(m => m.TrackAsync(8, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MixtapeTrack("private-album", "secret.mp3", "Secret", "Someone", "Private"));

        var result = await Caligo().QueueAsync(new QueueTrackCommand(8), Reader);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        _engine.Verify(e => e.QueueToRadioAsync(It.IsAny<RadioQueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APrivateAlbumOfSomeoneElse_StaysOutOfTheRotation()
    {
        _viewer.Setup(v => v.HiddenAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new HiddenMusic(Array.Empty<int>(), Array.Empty<int>(), new[] { 3 }, Array.Empty<int>()));
        _mixtape.Setup(m => m.AlbumFolderAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync("private-album");

        var result = await Caligo().RotationAsync(new RotationCommand(3, null, null), Admin);

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("100", 100)]
    [InlineData("-5", 0)]
    [InlineData("later", 0)]
    public async Task TheReleaseCursor_IsWhereThePageStarts(string? cursor, int offset)
    {
        var catalog = new Mock<ICatalogReader>();

        catalog.Setup(c => c.GetReleasesAsync(null, 4, offset, CatalogService.MaxPageSize, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PagedResult<ReleaseSummaryDto>());

        await new CatalogService(catalog.Object).GetReleasesAsync("  ", 4, cursor, 1000);

        catalog.VerifyAll();
    }

    [Fact]
    public async Task OnlyAdmins_ChangeTheRadioRotation()
    {
        var result = await Caligo().RotationAsync(new RotationCommand(3, null, null), Reader);

        Assert.Equal(ResultStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task AnAlbum_JoinsTheRotationWholeThroughItsFolder()
    {
        _mixtape.Setup(m => m.AlbumFolderAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync("arctic-monkeys-am");
        _engine.Setup(e => e.AddToRotationAsync(new RotationRequest("arctic-monkeys-am", null, null, null, null), It.IsAny<CancellationToken>()))
               .Returns(Lines("SUCCESS. The radio reloads its rotation."));

        var result = await Caligo().RotationAsync(new RotationCommand(3, null, null), Admin);
        var lines = new List<string>();

        await foreach (var line in result.Value!)
        {
            lines.Add(line);
        }

        Assert.Single(lines);
        _sync.Verify(s => s.Request(), Times.Once);
    }

    [Fact]
    public async Task WithoutCaligo_TheActionsSaySo()
    {
        _engine.Setup(e => e.IsConfigured).Returns(false);

        Assert.Equal(ResultStatus.Unavailable, Caligo().Ingest("x", Reader).Status);
        Assert.Equal(ResultStatus.Unavailable, (await Caligo().QueueAsync(new QueueTrackCommand(1), Reader)).Status);
        Assert.Equal(ResultStatus.Unavailable, (await Caligo().AskAsync(new AskCommand("x", null), Reader)).Status);
    }

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();

            yield return line;
        }
    }
}
