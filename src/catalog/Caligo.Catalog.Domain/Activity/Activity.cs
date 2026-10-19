namespace Caligo.Catalog.Domain.Activity;

public class UserContentInteraction
{
    public const float MinRating = 0f;
    public const float MaxRating = 10f;

    public int ContentId { get; set; }

    public int UserId { get; set; }

    public bool? Liked { get; set; }

    public float? Rating { get; set; }

    public static bool IsValidRating(float? rating) =>
        rating is null || (rating >= MinRating && rating <= MaxRating && !float.IsNaN(rating.Value));
}

public class WatchHistory
{
    public int WatchedContentId { get; set; }

    public int ContentId { get; set; }

    public int UserId { get; set; }

    public float? Timestamp { get; set; }

    public DateTime Date { get; set; }
}

public class Comment
{
    public const int MaxLength = 4000;

    public int CommentId { get; set; }

    public int UserId { get; set; }

    public int ContentId { get; set; }

    public string Text { get; set; } = string.Empty;

    public static string? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "A comment cannot be empty.";
        }

        return text.Trim().Length > MaxLength
            ? $"A comment can be at most {MaxLength} characters."
            : null;
    }
}

public class NotificationReason
{
    public int ReasonId { get; set; }

    public string ReasonDescription { get; set; } = string.Empty;
}

public class Notification
{
    public int NotificationId { get; set; }

    public int UserId { get; set; }

    public int ReasonId { get; set; }

    public DateTime NotificationDate { get; set; }
}

public class CurrentContentRecommendation
{
    public int RecommendationId { get; set; }

    public int UserId { get; set; }

    public int ContentId { get; set; }

    public string? RecommendationReason { get; set; }

    public DateTime? RecommendationDate { get; set; }
}
