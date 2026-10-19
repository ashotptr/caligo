namespace Blog.Catalog.Domain.Users;

public enum Gender
{
    Male,
    Female,
    Other
}

public class StreamingUser
{
    public const int MaxNameLength = 50;
    public const int MaxEmailLength = 50;
    public const int MinimumAge = 13;
    public const int SubscriptionDays = 30;

    public const string ExternalPassword = "managed-by-blog";

    public int UserId { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public DateOnly Dob { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public Gender Gender { get; set; }

    public int SubscriptionPlanId { get; set; }

    public DateOnly SubscriptionStartDate { get; set; }

    public DateOnly SubscriptionEndDate { get; set; }

    public bool IsSubscribedOn(DateOnly day) => day >= SubscriptionStartDate && day <= SubscriptionEndDate;

    public static string? Validate(string? firstName, string? lastName, DateOnly dob, string email, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > MaxNameLength)
        {
            return $"First name is required and can be at most {MaxNameLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > MaxNameLength)
        {
            return $"Last name is required and can be at most {MaxNameLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength)
        {
            return $"The account email must be at most {MaxEmailLength} characters to join the catalog.";
        }

        if (dob > today.AddYears(-MinimumAge))
        {
            return $"You must be at least {MinimumAge} years old.";
        }

        if (dob < today.AddYears(-130))
        {
            return "That date of birth is not valid.";
        }

        return null;
    }

    public static StreamingUser Create(
        string firstName, string lastName, DateOnly dob, string email, Gender gender, int subscriptionPlanId, DateOnly today) =>
        new()
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Dob = dob,
            Email = email,
            Password = ExternalPassword,
            Gender = gender,
            SubscriptionPlanId = subscriptionPlanId,
            SubscriptionStartDate = today,
            SubscriptionEndDate = today.AddDays(SubscriptionDays)
        };

    public void Renew(int subscriptionPlanId, DateOnly today)
    {
        SubscriptionPlanId = subscriptionPlanId;

        if (!IsSubscribedOn(today))
        {
            SubscriptionStartDate = today;
            SubscriptionEndDate = today.AddDays(SubscriptionDays);

            return;
        }

        SubscriptionEndDate = SubscriptionEndDate.AddDays(SubscriptionDays);
    }
}

public class SubscriptionPlan
{
    public int SubscriptionPlanId { get; set; }

    public string PaymentInfo { get; set; } = string.Empty;

    public string PlanInfo { get; set; } = string.Empty;
}

public class Payment
{
    public int PaymentId { get; set; }

    public int UserId { get; set; }

    public DateTime Date { get; set; }

    public float Amount { get; set; }

    public int SubscriptionPlanId { get; set; }
}

public class BlogUser
{
    public int UserId { get; set; }

    public string IdentityId { get; set; } = string.Empty;
}
