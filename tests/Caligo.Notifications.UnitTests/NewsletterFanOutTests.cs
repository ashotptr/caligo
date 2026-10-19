using Microsoft.Extensions.Options;
using Moq;
using Caligo.Notifications.Core;
using Caligo.Notifications.Host.Messaging;
using Xunit;

namespace Caligo.Notifications.UnitTests;

public class NewsletterFanOutTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    private readonly Mock<IDeliveryLedger> _ledger = new();
    private readonly Mock<ICommandQueue> _queue = new();
    private readonly List<Guid> _markedQueued = new();

    public NewsletterFanOutTests()
    {
        _ledger
            .Setup(l => l.MarkQueuedAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<Guid>, CancellationToken>((ids, _) => _markedQueued.AddRange(ids))
            .Returns(Task.CompletedTask);

        _queue.Setup(q => q.EnqueueAsync(It.IsAny<SendEmailCommand>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
    }

    private NewsletterFanOut FanOut(int chunkSize = 2) => new(
        _ledger.Object,
        _queue.Object,
        Options.Create(new NotificationsOptions { FanOutChunkSize = chunkSize }));

    private static SendEmailCommand Command() =>
        new(Guid.NewGuid(), EventId, Guid.NewGuid(), "reader@example.test", null, 1, "Title", "token");

    [Fact]
    public async Task EachChunkStartsWhereThePreviousOneEnded_UntilTheLedgerRunsOut()
    {
        var firstCursor = Guid.NewGuid();

        _ledger.Setup(l => l.ClaimChunkAsync(EventId, 1, "Title", Guid.Empty, 2, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FanOutChunk(new[] { Command(), Command() }, firstCursor));

        _ledger.Setup(l => l.ClaimChunkAsync(EventId, 1, "Title", firstCursor, 2, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FanOutChunk(new[] { Command() }, null));

        var result = await FanOut().RunAsync(EventId, 1, "Title");

        Assert.Equal(new FanOutResult(3, 3), result);
        Assert.Equal(3, _markedQueued.Count);
    }

    [Fact]
    public async Task OnlyCommandsTheBrokerAccepted_AreMarkedQueued()
    {
        var accepted = Command();
        var rejected = Command();

        _ledger.Setup(l => l.ClaimChunkAsync(EventId, 1, "Title", Guid.Empty, 2, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FanOutChunk(new[] { accepted, rejected }, null));

        _queue.Setup(q => q.EnqueueAsync(rejected, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await FanOut().RunAsync(EventId, 1, "Title");

        Assert.Equal(new FanOutResult(2, 1), result);
        Assert.Equal(new[] { accepted.DeliveryId }, _markedQueued);
    }

    [Fact]
    public async Task AReplayedEvent_ClaimsNothing_AndSendsNothing()
    {
        _ledger.Setup(l => l.ClaimChunkAsync(EventId, 1, "Title", Guid.Empty, 2, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FanOutChunk(Array.Empty<SendEmailCommand>(), null));

        var result = await FanOut().RunAsync(EventId, 1, "Title");

        Assert.Equal(new FanOutResult(0, 0), result);

        _queue.Verify(q => q.EnqueueAsync(It.IsAny<SendEmailCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
