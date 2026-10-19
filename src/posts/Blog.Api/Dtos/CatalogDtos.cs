using System.ComponentModel.DataAnnotations;

namespace Blog.Api.Dtos;

public class InteractionRequest
{
    public bool? Liked { get; set; }

    public float? Rating { get; set; }
}

public class CommentRequest
{
    [Required]
    public string Comment { get; set; } = string.Empty;
}

public class WatchRequest
{
    public float? Timestamp { get; set; }
}

public class StreamingProfileRequest
{
    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    public DateOnly DateOfBirth { get; set; }

    [Required]
    public string Gender { get; set; } = string.Empty;

    public int SubscriptionPlanId { get; set; }
}

public class SubscriptionRequest
{
    public int SubscriptionPlanId { get; set; }
}
