using System.Globalization;
using System.Text.Json;
using Blog.Catalog.Domain.Users;

namespace Blog.Catalog.Application.Accounts;

public record PlanDto(int SubscriptionPlanId, string PaymentInfo, string PlanInfo, float MonthlyPrice);

public record StreamingProfileDto(
    int UserId,
    string? FirstName,
    string? LastName,
    DateOnly Dob,
    string Email,
    string Gender,
    int SubscriptionPlanId,
    DateOnly SubscriptionStartDate,
    DateOnly SubscriptionEndDate,
    bool Active)
{
    public static StreamingProfileDto From(StreamingUser user, DateOnly today) => new(
        user.UserId,
        user.FirstName,
        user.LastName,
        user.Dob,
        user.Email,
        user.Gender.ToString(),
        user.SubscriptionPlanId,
        user.SubscriptionStartDate,
        user.SubscriptionEndDate,
        user.IsSubscribedOn(today));
}

public record CreateProfileCommand(
    string? FirstName, string? LastName, DateOnly Dob, string? Gender, int SubscriptionPlanId);

public record PaymentDto(int PaymentId, DateTime Date, float Amount, int SubscriptionPlanId);

public record SpendingDto(float AllTimeSpending, List<PaymentDto> Payments);

public static class PlanPricing
{
    public static float MonthlyPrice(string paymentInfo)
    {
        try
        {
            using var document = JsonDocument.Parse(paymentInfo);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("monthly_price", out var price))
            {
                return 0f;
            }

            if (price.ValueKind == JsonValueKind.Number)
            {
                return price.GetSingle();
            }

            var digits = new string((price.GetString() ?? string.Empty)
                .Where(c => char.IsAsciiDigit(c) || c == '.')
                .ToArray());

            return float.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                ? amount
                : 0f;
        }
        catch (JsonException)
        {
            return 0f;
        }
    }
}
