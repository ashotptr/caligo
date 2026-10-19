using Microsoft.Extensions.Time.Testing;
using Moq;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Application.Common;
using Caligo.Posts.Application.Posts;
using Caligo.Posts.Domain.Events;
using Caligo.Posts.Domain.Posts;
using Caligo.SharedKernel;
using Xunit;

namespace Caligo.Posts.UnitTests;

public class PostServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPostRepository> _repository = new(MockBehavior.Strict);
    private readonly FakePostCache _cache = new();
    private readonly Mock<IPostSearch> _search = new(MockBehavior.Strict);
    private readonly Mock<IAnalyticsPublisher> _analytics = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly PostingRules _rules = new() { MaxScheduledPerAuthor = 3 };
    private readonly Mock<IContentLookup> _contents = new();

    private PostService Service() =>
        new(_repository.Object, _cache, _search.Object, _analytics.Object, _time, _rules, _contents.Object);

    private static PostDetailDto Published(int id, string? authorId = "author-1") =>
        new() { Id = id, AuthorId = authorId, Status = PostStatuses.Published };

    private static PostDetailDto Scheduled(int id, string? authorId = "author-1") =>
        new() { Id = id, AuthorId = authorId, Status = PostStatuses.Scheduled };

    [Fact]
    public async Task GetPage_ServedFromCache_DoesNotTouchTheDatabase()
    {
        _cache.Seed(
            "posts:list:0:page:first:20",
            new PagedResult<PostSummaryDto> { Items = new() { new PostSummaryDto { Id = 7 } } });

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
        var page = new PagedResult<PostSummaryDto>();

        _repository.Setup(r => r.GetPageAsync(null, 20, null, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await Service().GetPageAsync(null, 20, false, Caller.Anonymous);

        Assert.True(result.IsSuccess);
        Assert.True(_cache.Contains("posts:list:0:page:first:20"));
    }

    [Fact]
    public async Task GetPage_AfterAWrite_UsesTheNextGeneration_SoStalePagesAreNeverRead()
    {
        _cache.Seed("posts:list:0:page:first:20", new PagedResult<PostSummaryDto>());

        await _cache.InvalidateAsync(PostService.ListScope);

        _repository.Setup(r => r.GetPageAsync(null, 20, null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PagedResult<PostSummaryDto>());

        await Service().GetPageAsync(null, 20, false, Caller.Anonymous);

        Assert.Contains("posts:list:1:page:first:20", _cache.Loaded);
    }

    [Fact]
    public async Task GetPage_ForMyPostsOnly_IsNeverCached()
    {
        var page = new PagedResult<PostSummaryDto>();

        _repository.Setup(r => r.GetPageAsync(null, 20, "user-1", It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await Service().GetPageAsync(null, 20, mineOnly: true, new Caller("user-1", false));

        Assert.True(result.IsSuccess);
        Assert.Empty(_cache.Loaded);
        Assert.Equal(0, _cache.Count);
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
        _repository.Setup(r => r.GetPageAsync(null, expected, null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PagedResult<PostSummaryDto>());

        await Service().GetPageAsync(null, requested, false, Caller.Anonymous);

        _repository.Verify(r => r.GetPageAsync(null, expected, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_CountsAView_WhenThePostExists()
    {
        _cache.Seed("posts:detail:42:0", Published(42));

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
        _repository.Setup(r => r.GetDetailAsync(999, It.IsAny<CancellationToken>()))
                   .ReturnsAsync((PostDetailDto?)null);

        var result = await Service().GetAsync(999, ViewContext.Empty, Caller.Anonymous);

        Assert.Equal(ResultStatus.NotFound, result.Status);

        _analytics.Verify(a => a.PublishPostViewed(It.IsAny<PostViewedEvent>()), Times.Never);
    }

    [Fact]
    public async Task Get_APublishedPost_IsCached()
    {
        _repository.Setup(r => r.GetDetailAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Published(5));

        await Service().GetAsync(5, ViewContext.Empty, Caller.Anonymous);

        Assert.True(_cache.Contains("posts:detail:5:0"));
    }

    [Fact]
    public async Task Get_AScheduledPost_IsHiddenFromStrangers_AndNeverCached()
    {
        _repository.Setup(r => r.GetDetailAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync(Scheduled(6));

        var result = await Service().GetAsync(6, ViewContext.Empty, new Caller("someone-else", false));

        Assert.Equal(ResultStatus.NotFound, result.Status);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public async Task Get_AScheduledPost_IsVisibleToItsAuthor_WithoutCountingAView()
    {
        _repository.Setup(r => r.GetDetailAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync(Scheduled(6));

        var result = await Service().GetAsync(6, ViewContext.Empty, new Caller("author-1", false));

        Assert.True(result.IsSuccess);

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
    public async Task Create_InvalidatesTheListPages_SoTheNewPostIsVisibleImmediately()
    {
        _repository.Setup(r => r.Add(It.IsAny<BlogPost>()));
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Published(1));

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", new[] { "Tag" }), new Caller("user-1", false));

        Assert.True(result.IsSuccess);
        Assert.Contains(PostService.ListScope, _cache.Invalidated);
    }

    [Fact]
    public async Task Create_AReviewOfAnUnknownTitle_IsInvalid()
    {
        _contents.Setup(c => c.ExistsAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, ContentId: 77), new Caller("user-1", false));

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Equal(PostService.UnknownContent, result.Error);
    }

    [Fact]
    public async Task Create_AReview_KeepsItsLinkToTheTitle_AndAnnouncesIt()
    {
        BlogPost? captured = null;

        _contents.Setup(c => c.ExistsAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _contents.Setup(c => c.GetTitlesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Dictionary<int, string> { [77] = "Fight Club" });
        _repository.Setup(r => r.Add(It.IsAny<BlogPost>())).Callback<BlogPost>(p => captured = p);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PostDetailDto { ContentId = 77 });

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, ContentId: 77), new Caller("user-1", false));

        Assert.True(result.IsSuccess);
        Assert.Equal("Fight Club", result.Value!.ContentTitle);
        Assert.Equal(77, captured!.ContentId);

        var published = Assert.IsType<PostPublished>(Assert.Single(captured.DrainDomainEvents()));

        Assert.Equal(77, published.ContentId);
    }

    [Fact]
    public async Task Reviews_AreLoadedByTitle_WithTheTitleFilledIn()
    {
        _repository.Setup(r => r.GetPageForContentAsync(77, null, 20, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PagedResult<PostSummaryDto>
                   {
                       Items = new() { new PostSummaryDto { Id = 5, ContentId = 77 } }
                   });
        _contents.Setup(c => c.GetTitlesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Dictionary<int, string> { [77] = "Fight Club" });

        var result = await Service().GetReviewsAsync(77, null, 20);

        Assert.True(result.IsSuccess);
        Assert.Equal("Fight Club", Assert.Single(result.Value!.Items).ContentTitle);
    }

    [Fact]
    public async Task Create_StampsThePost_WithTheInjectedClock()
    {
        BlogPost? captured = null;

        _repository.Setup(r => r.Add(It.IsAny<BlogPost>())).Callback<BlogPost>(p => captured = p);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PostDetailDto());

        await Service().CreateAsync(new WritePostCommand("Title", "Content", null), new Caller("user-1", false));

        Assert.NotNull(captured);
        Assert.Equal(Now.UtcDateTime, captured!.PublishedDate);
    }

    [Fact]
    public async Task Create_WithAPublishTimeInThePast_PublishesNow()
    {
        BlogPost? captured = null;

        _repository.Setup(r => r.Add(It.IsAny<BlogPost>())).Callback<BlogPost>(p => captured = p);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new PostDetailDto());

        await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, Now.UtcDateTime.AddHours(-1)), new Caller("user-1", false));

        Assert.True(captured!.IsPublished);
    }

    [Fact]
    public async Task Create_WithAFuturePublishTime_SchedulesThePost_AndLeavesTheFeedAlone()
    {
        BlogPost? captured = null;
        var publishAt = Now.UtcDateTime.AddHours(2);

        _repository.Setup(r => r.TryAddScheduledAsync(It.IsAny<BlogPost>(), 3, It.IsAny<CancellationToken>()))
                   .Callback<BlogPost, int, CancellationToken>((p, _, _) => captured = p)
                   .ReturnsAsync(true);
        _repository.Setup(r => r.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Scheduled(1));

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, publishAt), new Caller("user-1", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(PostStatus.Scheduled, captured!.Status);
        Assert.Equal(publishAt, captured.ScheduledFor);
        Assert.False(captured.HasPendingEvents);
        Assert.Empty(_cache.Invalidated);
    }

    [Fact]
    public async Task Create_OverTheSchedulingQuota_IsAConflict()
    {
        _repository.Setup(r => r.TryAddScheduledAsync(It.IsAny<BlogPost>(), 3, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, Now.UtcDateTime.AddDays(1)), new Caller("user-1", false));

        Assert.Equal(ResultStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task Create_ScheduledTooFarAhead_IsRejected_WithoutTouchingTheDatabase()
    {
        var result = await Service().CreateAsync(
            new WritePostCommand("Title", "Content", null, Now.UtcDateTime.AddYears(2)), new Caller("user-1", false));

        Assert.Equal(ResultStatus.Invalid, result.Status);
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

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Moderated", "Content", null), new Caller("admin", true));

        Assert.True(result.IsSuccess);
        Assert.Equal("Moderated", post.Title);
    }

    [Fact]
    public async Task Update_InvalidatesThatPost_AndTheListPages()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await Service().UpdateAsync(1, new WritePostCommand("Edited", "Content", null), new Caller("author-1", false));

        Assert.Equal(new[] { PostService.PostScope(1), PostService.ListScope }, _cache.Invalidated);
    }

    [Fact]
    public async Task Update_AMissingPost_IsNotFound()
    {
        _repository.Setup(r => r.FindAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync((BlogPost?)null);

        var result = await Service().UpdateAsync(404, new WritePostCommand("t", "c", null), new Caller("u", true));

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Update_WithAStaleExpectedVersion_FailsThePrecondition_AndSavesNothing()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);
        post.Revise("Edited elsewhere", "Content", null);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Mine", "Content", null, ExpectedVersion: 0), new Caller("author-1", false));

        Assert.Equal(ResultStatus.PreconditionFailed, result.Status);
        Assert.Equal("Edited elsewhere", post.Title);

        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ThatLosesARaceWithAnotherWriter_IsAConflict_AndTheCacheIsLeftAlone()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ConcurrencyConflictException("lost the race"));

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Mine", "Content", null), new Caller("author-1", false));

        Assert.Equal(ResultStatus.Conflict, result.Status);
        Assert.Empty(_cache.Invalidated);
    }

    [Fact]
    public async Task Update_CannotRescheduleAPublishedPost()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);

        var result = await Service().UpdateAsync(
            1, new WritePostCommand("Title", "Content", null, Now.UtcDateTime.AddDays(1)), new Caller("author-1", false));

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Delete_ByTheAuthor_RemovesThePost_AndInvalidatesItsCaches()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.Remove(post));
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await Service().DeleteAsync(1, new Caller("author-1", false));

        Assert.True(result.IsSuccess);

        _repository.Verify(r => r.Remove(post), Times.Once);

        Assert.Equal(new[] { PostService.PostScope(1), PostService.ListScope }, _cache.Invalidated);
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

    [Fact]
    public async Task Delete_RacingAnUpdate_IsAConflict_NotAServerError()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now.UtcDateTime);

        _repository.Setup(r => r.FindAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(post);
        _repository.Setup(r => r.Remove(post));
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ConcurrencyConflictException("changed underneath"));

        var result = await Service().DeleteAsync(1, new Caller("author-1", false));

        Assert.Equal(ResultStatus.Conflict, result.Status);
    }
}
