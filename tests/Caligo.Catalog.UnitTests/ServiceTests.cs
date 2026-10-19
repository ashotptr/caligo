using Microsoft.Extensions.Time.Testing;
using Moq;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Users;
using Caligo.SharedKernel;
using Xunit;

namespace Caligo.Catalog.UnitTests;

public class ServiceTests
{
    private static readonly Caller Reader = new("identity-1", false);

    private readonly Mock<ICatalogReader> _catalog = new();
    private readonly Mock<IStreamingAccounts> _accounts = new();
    private readonly Mock<IActivityStore> _activity = new();
    private readonly Mock<IBlogAccounts> _blog = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    private CatalogService Catalog() => new(_catalog.Object);

    private StreamingAccountService Accounts() => new(_accounts.Object, _blog.Object, _time);

    private ActivityService Activity() => new(_catalog.Object, _accounts.Object, _activity.Object, _time);

    [Fact]
    public async Task AnUnknownKind_IsRejectedBeforeTheDatabaseIsAsked()
    {
        var result = await Catalog().GetPageAsync("podcast", null, null, null, 20);

        Assert.Equal(ResultStatus.Invalid, result.Status);
        _catalog.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, CatalogService.MaxPageSize)]
    public async Task ThePageSize_IsClamped(int requested, int expected)
    {
        _catalog.Setup(c => c.GetContentPageAsync(null, null, null, null, expected, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagedResult<ContentSummaryDto>());

        var result = await Catalog().GetPageAsync(null, null, "  ", null, requested);

        Assert.True(result.IsSuccess);
        _catalog.Verify(c => c.GetContentPageAsync(null, null, null, null, expected, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SimilarTitlesOfAMissingTitle_AreNotFound()
    {
        _catalog.Setup(c => c.ContentExistsAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Catalog().GetSimilarAsync(9);

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task CreatingAProfile_WritesTheUserWithThePlanPriceAsItsFirstPayment()
    {
        _blog.Setup(b => b.GetEmailAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync("ada@example.test");
        _accounts.Setup(a => a.FindPlanAsync(4, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SubscriptionPlan { SubscriptionPlanId = 4, PaymentInfo = "{\"monthly_price\": \"€7.99\"}" });
        _accounts.Setup(a => a.CreateAsync(
                     It.IsAny<StreamingUser>(), "identity-1", It.IsAny<float>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((StreamingUser user, string _, float _, DateTime _, CancellationToken _) => user);

        var result = await Accounts().CreateProfileAsync(
            Reader, new CreateProfileCommand("Ada", "Lovelace", new DateOnly(1990, 1, 1), "female", 4));

        Assert.True(result.IsSuccess);
        Assert.Equal("Female", result.Value!.Gender);
        Assert.True(result.Value.Active);

        _accounts.Verify(a => a.CreateAsync(
            It.Is<StreamingUser>(u => u.Email == "ada@example.test" && u.Password == StreamingUser.ExternalPassword),
            "identity-1",
            It.Is<float>(amount => Math.Abs(amount - 7.99f) < 0.001f),
            It.Is<DateTime>(paid => paid.Kind == DateTimeKind.Unspecified),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ASecondProfileForTheSameAccount_IsAConflict()
    {
        _accounts.Setup(a => a.FindUserIdAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync(21);

        var result = await Accounts().CreateProfileAsync(
            Reader, new CreateProfileCommand("Ada", "Lovelace", new DateOnly(1990, 1, 1), "Female", 4));

        Assert.Equal(ResultStatus.Conflict, result.Status);
    }

    [Theory]
    [InlineData("robot")]
    [InlineData("7")]
    [InlineData(null)]
    public async Task AGenderOutsideTheEnum_IsInvalid(string? gender)
    {
        var result = await Accounts().CreateProfileAsync(
            Reader, new CreateProfileCommand("Ada", "Lovelace", new DateOnly(1990, 1, 1), gender, 4));

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnEmailAlreadyInTheUserTable_IsAConflict()
    {
        _blog.Setup(b => b.GetEmailAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync("john.doe@gmail.com");
        _accounts.Setup(a => a.FindPlanAsync(4, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SubscriptionPlan { SubscriptionPlanId = 4, PaymentInfo = "{}" });
        _accounts.Setup(a => a.EmailTakenAsync("john.doe@gmail.com", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Accounts().CreateProfileAsync(
            Reader, new CreateProfileCommand("John", "Doe", new DateOnly(1985, 6, 15), "Male", 4));

        Assert.Equal(ResultStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task Anonymous_CannotCreateAProfileOrRate()
    {
        var profile = await Accounts().CreateProfileAsync(
            Caller.Anonymous, new CreateProfileCommand("Ada", "Lovelace", new DateOnly(1990, 1, 1), "Female", 4));

        var rating = await Activity().SetInteractionAsync(3, new SetInteractionCommand(true, 8f), Caller.Anonymous);

        Assert.Equal(ResultStatus.Unauthorized, profile.Status);
        Assert.Equal(ResultStatus.Unauthorized, rating.Status);
    }

    [Fact]
    public async Task RatingWithoutAStreamingProfile_AsksForOne()
    {
        _catalog.Setup(c => c.ContentExistsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Activity().SetInteractionAsync(3, new SetInteractionCommand(true, 8f), Reader);

        Assert.Equal(ResultStatus.Conflict, result.Status);
        Assert.Equal(StreamingAccountService.ProfileRequired, result.Error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 11f)]
    [InlineData(true, -1f)]
    public async Task AnEmptyOrOutOfRangeRating_IsInvalid(bool? liked, float? rating)
    {
        var result = await Activity().SetInteractionAsync(3, new SetInteractionCommand(liked, rating), Reader);

        Assert.Equal(ResultStatus.Invalid, result.Status);
        _activity.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoggingAWatch_StoresTheCurrentTimeWithoutATimeZone()
    {
        _catalog.Setup(c => c.ContentExistsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _accounts.Setup(a => a.FindUserIdAsync("identity-1", It.IsAny<CancellationToken>())).ReturnsAsync(21);
        _activity.Setup(a => a.LogWatchAsync(3, 21, 12.5f, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new WatchDto(90, 3, "Fight Club", ContentKinds.Movie, 12.5f, default));

        var result = await Activity().LogWatchAsync(3, new WatchCommand(12.5f), Reader);

        Assert.True(result.IsSuccess);

        _activity.Verify(a => a.LogWatchAsync(
            3,
            21,
            12.5f,
            It.Is<DateTime>(d => d.Kind == DateTimeKind.Unspecified && d == new DateTime(2026, 10, 13, 12, 0, 0)),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ACommentOnAMissingTitle_IsNotFound()
    {
        _catalog.Setup(c => c.ContentExistsAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Activity().AddCommentAsync(404, "Hello", Reader);

        Assert.Equal(ResultStatus.NotFound, result.Status);
    }
}
