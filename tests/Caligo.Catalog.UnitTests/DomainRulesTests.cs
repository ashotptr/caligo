using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Activity;
using Caligo.Catalog.Domain.Contents;
using Caligo.Catalog.Domain.Users;
using Xunit;

namespace Caligo.Catalog.UnitTests;

public class DomainRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    [Fact]
    public void ATitleLongerThanTheColumn_IsCutToFiftyCharacters()
    {
        var title = new string('a', 80);

        Assert.Equal(Content.MaxTitleLength, Content.FitTitle(title).Length);
        Assert.Equal("Short", Content.FitTitle("  Short "));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0f, true)]
    [InlineData(10f, true)]
    [InlineData(7.5f, true)]
    [InlineData(-0.1f, false)]
    [InlineData(10.1f, false)]
    [InlineData(float.NaN, false)]
    public void ARating_MustStayBetweenZeroAndTen(float? rating, bool valid)
    {
        Assert.Equal(valid, UserContentInteraction.IsValidRating(rating));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyComment_IsRefused(string? text)
    {
        Assert.NotNull(Comment.Validate(text));
    }

    [Fact]
    public void ACommentWithinTheLimit_IsAccepted()
    {
        Assert.Null(Comment.Validate("Great episode."));
        Assert.NotNull(Comment.Validate(new string('x', Comment.MaxLength + 1)));
    }

    [Fact]
    public void AProfile_NeedsNamesAnAdultEnoughBirthDateAndAnEmailThatFitsTheColumn()
    {
        Assert.Null(StreamingUser.Validate("Ada", "Lovelace", new DateOnly(1990, 1, 1), "ada@example.test", Today));
        Assert.NotNull(StreamingUser.Validate(" ", "Lovelace", new DateOnly(1990, 1, 1), "ada@example.test", Today));
        Assert.NotNull(StreamingUser.Validate("Ada", null, new DateOnly(1990, 1, 1), "ada@example.test", Today));
        Assert.NotNull(StreamingUser.Validate("Ada", "Lovelace", Today.AddYears(-5), "ada@example.test", Today));
        Assert.NotNull(StreamingUser.Validate("Ada", "Lovelace", new DateOnly(1800, 1, 1), "ada@example.test", Today));
        Assert.NotNull(StreamingUser.Validate(
            "Ada", "Lovelace", new DateOnly(1990, 1, 1), new string('a', 60) + "@example.test", Today));
    }

    [Fact]
    public void ANewProfile_StartsItsSubscriptionTodayAndNeverStoresARealPassword()
    {
        var user = StreamingUser.Create("Ada", "Lovelace", new DateOnly(1990, 1, 1), "ada@example.test", Gender.Female, 4, Today);

        Assert.Equal(Today, user.SubscriptionStartDate);
        Assert.Equal(Today.AddDays(StreamingUser.SubscriptionDays), user.SubscriptionEndDate);
        Assert.Equal(StreamingUser.ExternalPassword, user.Password);
        Assert.True(user.IsSubscribedOn(Today));
        Assert.False(user.IsSubscribedOn(Today.AddDays(StreamingUser.SubscriptionDays + 1)));
    }

    [Fact]
    public void RenewingAnActiveSubscription_ExtendsIt_AndRenewingALapsedOne_StartsAgain()
    {
        var user = StreamingUser.Create("Ada", "Lovelace", new DateOnly(1990, 1, 1), "ada@example.test", Gender.Female, 4, Today);
        var originalEnd = user.SubscriptionEndDate;

        user.Renew(5, Today.AddDays(3));

        Assert.Equal(5, user.SubscriptionPlanId);
        Assert.Equal(Today, user.SubscriptionStartDate);
        Assert.Equal(originalEnd.AddDays(StreamingUser.SubscriptionDays), user.SubscriptionEndDate);

        var later = user.SubscriptionEndDate.AddDays(10);

        user.Renew(4, later);

        Assert.Equal(later, user.SubscriptionStartDate);
        Assert.Equal(later.AddDays(StreamingUser.SubscriptionDays), user.SubscriptionEndDate);
    }

    [Theory]
    [InlineData("{\"monthly_price\": \"€7.99\"}", 7.99f)]
    [InlineData("{\"monthly_price\": 12.5}", 12.5f)]
    [InlineData("{\"other\": 1}", 0f)]
    [InlineData("not json", 0f)]
    public void ThePlanPrice_IsReadFromThePaymentInfoAsStored(string paymentInfo, float expected)
    {
        Assert.Equal(expected, PlanPricing.MonthlyPrice(paymentInfo), 3);
    }

    [Fact]
    public void ACursor_SurvivesARoundTrip_AndRubbishIsIgnored()
    {
        var cursor = new ContentCursor(new DateOnly(2014, 10, 3), 42);

        Assert.Equal(cursor, ContentCursor.TryDecode(cursor.Encode()));
        Assert.Null(ContentCursor.TryDecode(null));
        Assert.Null(ContentCursor.TryDecode("%%%"));
        Assert.Null(ContentCursor.TryDecode("YWJj"));
    }
}
