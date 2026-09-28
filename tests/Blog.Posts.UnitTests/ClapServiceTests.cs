using Microsoft.Extensions.Time.Testing;
using Moq;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Claps;
using Blog.Posts.Application.Common;
using Blog.Posts.Application.Posts;
using Xunit;

namespace Blog.Posts.UnitTests;

public class ClapServiceTests
{
    private readonly Mock<IPostRepository> _posts = new(MockBehavior.Strict);
    private readonly Mock<IClapStore> _claps = new(MockBehavior.Strict);
    private readonly Mock<IAnalyticsPublisher> _analytics = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private ClapService Service() => new(_posts.Object, _claps.Object, _analytics.Object, _time);

    private void PostExists(int id, string authorId, bool published = true) =>
        _posts.Setup(p => p.GetHeaderAsync(id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PostHeader(id, authorId, published));

    [Fact]
    public async Task Clapping_RequiresSigningIn()
    {
        var result = await Service().ClapAsync(1, 5, Caller.Anonymous);

        Assert.Equal(ResultStatus.Unauthorized, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task ACountOutsideOneToFifty_IsRejected(int count)
    {
        var result = await Service().ClapAsync(1, count, new Caller("reader", false));

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnAuthor_CannotClapForTheirOwnPost()
    {
        PostExists(1, "author-1");

        var result = await Service().ClapAsync(1, 5, new Caller("author-1", false));

        Assert.Equal(ResultStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task AScheduledPost_CannotBeClapped()
    {
        PostExists(1, "author-1", published: false);

        var result = await Service().ClapAsync(1, 5, new Caller("reader", false));

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task AcceptedClaps_AreStreamedToAnalytics_WithTheAppliedDelta()
    {
        PostExists(1, "author-1");

        _claps.Setup(c => c.ClapAsync(1, "reader", 10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ClapOutcome(4, 50, false));

        var result = await Service().ClapAsync(1, 10, new Caller("reader", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.Applied);
        Assert.Equal(50, result.Value.Mine);

        _analytics.Verify(a => a.PublishPostClapped(It.Is<PostClappedEvent>(e =>
            e.PostId == 1 && e.UserId == "reader" && e.Delta == 4 && e.ReaderTotal == 50)), Times.Once);
    }

    [Fact]
    public async Task ClapsPastTheCap_AreANoOp_AndPublishNothing()
    {
        PostExists(1, "author-1");

        _claps.Setup(c => c.ClapAsync(1, "reader", 5, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ClapOutcome(0, 50, false));

        var result = await Service().ClapAsync(1, 5, new Caller("reader", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.Applied);

        _analytics.Verify(a => a.PublishPostClapped(It.IsAny<PostClappedEvent>()), Times.Never);
    }
}
