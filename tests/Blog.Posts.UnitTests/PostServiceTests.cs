using Microsoft.Extensions.Time.Testing;
using Moq;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Common;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Xunit;

namespace Blog.Posts.UnitTests;

public class PostServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPostRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IPostCache> _cache = new(MockBehavior.Strict);
    private readonly Mock<IPostSearch> _search = new(MockBehavior.Strict);
    private readonly Mock<IAnalyticsPublisher> _analytics = new();
    private readonly FakeTimeProvider _time = new(Now);

    private PostService Service() =>
        new(_repository.Object, _cache.Object, _search.Object, _analytics.Object, _time);

    private void ExpectCacheVersion(string version = "v1") =>
        _cache.Setup(c => c.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(version);

    [Fact]
    public async Task GetPage_ServedFromCache_DoesNotTouchTheDatabase()
    {
        ExpectCacheVersion();

        var cached = new PagedResult<PostSummaryDto> { Items = new() { new PostSummaryDto { Id = 7 } } };

        _cache.Setup(c => c.GetAsync<PagedResult<PostSummaryDto>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(cached);

        var result = await Service().GetPageAsync(null, 20, false, Caller.Anonymous);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.Items[0].Id);

        _repository.Verify(
            r => r.GetPageAsync(It.IsAny<PostCursor?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPage_OnACacheMiss_ReadsThroughAndStoresTheResult()
    {
        ExpectCacheVersion();

        _cache.Setup(c => c.GetAsync<PagedResult<PostSummaryDto>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((PagedResult<PostSummaryDto>?)null);

        var page = new PagedResult<PostSummaryDto>();

        _repository.Setup(r => r.GetPageAsync(null, 20, null, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        _cache.Setup(c => c.SetAsync(It.IsAny<string>(), page, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var result = await Service().GetPageAsync(null, 20, false, Caller.Anonymous);

        Assert.True(result.IsSuccess);

        _cache.Verify(
            c => c.SetAsync(It.IsAny<string>(), page, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetPage_ForMyPostsOnly_IsNeverCached()
    {
        ExpectCacheVersion();

        var page = new PagedResult<PostSummaryDto>();

        _repository.Setup(r => r.GetPageAsync(null, 20, "user-1", It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await Service().GetPageAsync(null, 20, mineOnly: true, new Caller("user-1", false));

        Assert.True(result.IsSuccess);

        _cache.Verify(
            c => c.GetAsync<PagedResult<PostSummaryDto>>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cache.Verify(
            c => c.SetAsync(It.IsAny<string>(), It.IsAny<PagedResult<PostSummaryDto>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPage_ForMyPostsOnly_RequiresAuthentication()
    {
        var result = await Service().GetPageAsync(null, 20, mineOnly: true, Caller.Anonymous);

        Assert.Equal(ResultStatus.Unauthorized, result.Status);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100_000, PostService.MaxPageSize)]
    public async Task GetPage_ClampsTheRequestedLimit(int requested, int expected)
    {
        ExpectCacheVersion();

        _cache.Setup(c => c.GetAsync<PagedResult<PostSummaryDto>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((PagedResult<PostSummaryDto>?)null);

        _repository.Setup(r => r.GetPageAsync(null, expected, null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PagedResult<PostSummaryDto>());

        _cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<PagedResult<PostSummaryDto>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await Service().GetPageAsync(null, requested, false, Caller.Anonymous);

        _repository.Verify(r => r.GetPageAsync(null, expected, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_CountsAView_WhenThePostExists()
    {
        ExpectCacheVersion();

        _cache.Setup(c => c.GetAsync<PostDetailDto>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PostDetailDto { Id = 42 });

        var result = await Service().GetAsync(42, new ViewContext("https://ref", "hash"), new Caller("user-1", false));

        Assert.True(result.IsSuccess);

        _analytics.Verify(a => a.PublishPostViewed(It.Is<PostViewedEvent>(e =>
            e.PostId == 42 &&
            e.UserId == "user-1" &&
            e.VisitorHash == "hash" &&
            e.ViewedAt == Now.UtcDateTime)), Times.Once);
    }

    [Fact]
    public async Task Get_ForAMissingPost_CountsNothing()
    {
        ExpectCacheVersion();

        _cache.Setup(c => c.GetAsync<PostDetailDto>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((PostDetailDto?)null);

        _repository.Setup(r => r.GetDetailAsync(999, It.IsAny<CancellationToken>()))
                   .ReturnsAsync((PostDetailDto?)null);

        var result = await Service().GetAsync(999, ViewContext.Empty, Caller.Anonymous);

        Assert.Equal(ResultStatus.NotFound, result.Status);

        _analytics.Verify(a => a.PublishPostViewed(It.IsAny<PostViewedEvent>()), Times.Never);
    }

    [Fact]
    public async Task Search_WithABlankQuery_IsRejectedWithoutQueryingAnything()
    {
        var result = await Service().SearchAsync("   ", 20);

        Assert.Equal(ResultStatus.Invalid, result.Status);

        _search.Verify(
            s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Search_TrimsTheQuery_AndClampsTheLimit()
    {
        _search.Setup(s => s.SearchAsync("kafka", PostService.MaxSearchResults, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<PostSummaryDto>());

        var result = await Service().SearchAsync("  kafka  ", 5000);

        Assert.True(result.IsSuccess);

        _search.Verify(s => s.SearchAsync("kafka", PostService.MaxSearchResults, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_InvalidatesTheCache_SoTheNewPostIsVisibleImmediately()
    {
        _repository.Setup(r => r.Add(It.IsAny<BlogPost>()));
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PostDetailDto { Id = 1 });

        _cache.Setup(c => c.InvalidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", new[] { "Tag" }), new Caller("user-1", false));

        Assert.True(result.IsSuccess);

        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_StampsThePost_WithTheInjectedClock()
    {
        BlogPost? captured = null;

        _repository.Setup(r => r.Add(It.IsAny<BlogPost>())).Callback<BlogPost>(p => captured = p);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PostDetailDto());
        _cache.Setup(c => c.InvalidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Service().CreateAsync(new WritePostCommand("Title", "Content", null), new Caller("user-1", false));

        Assert.NotNull(captured);
        Assert.Equal(Now.UtcDateTime, captured!.PublishedDate);
    }

    [Fact]
    public async Task Update_ByAStranger_IsForbidden_AndNothingIsSaved()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Hijacked", "Content", null), new Caller("someone-else", false));

        Assert.Equal(ResultStatus.Forbidden, result.Status);
        Assert.Equal("Title", post.Title);

        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ByAnAdministrator_IsAllowed_EvenOnSomeoneElsesPost()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _cache.Setup(c => c.InvalidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Moderated", "Content", null), new Caller("admin", true));

        Assert.True(result.IsSuccess);
        Assert.Equal("Moderated", post.Title);
    }

    [Fact]
    public async Task Update_AMissingPost_IsNotFound()
    {
        _repository.Setup(r => r.FindAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync((BlogPost?)null);

        var result = await Service().UpdateAsync(404, new WritePostCommand("t", "c", null), new Caller("u", true));

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Delete_ByTheAuthor_RemovesThePost_AndInvalidatesTheCache()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.Remove(post));
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _cache.Setup(c => c.InvalidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await Service().DeleteAsync(1, new Caller("author-1", false));

        Assert.True(result.IsSuccess);

        _repository.Verify(r => r.Remove(post), Times.Once);
        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ByAStranger_IsForbidden_AndRemovesNothing()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);

        var result = await Service().DeleteAsync(1, new Caller("someone-else", false));

        Assert.Equal(ResultStatus.Forbidden, result.Status);

        _repository.Verify(r => r.Remove(It.IsAny<BlogPost>()), Times.Never);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
