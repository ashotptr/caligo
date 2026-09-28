using System.Text.Json.Serialization;

namespace Blog.Posts.Application.Posts;

public static class PostStatuses
{
    public const string Published = "published";

    public const string Scheduled = "scheduled";
}

public class PostSummaryDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string Status { get; set; } = PostStatuses.Published;
    public DateTime? ScheduledFor { get; set; }
}

public class PostDetailDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorId { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Status { get; set; } = PostStatuses.Published;
    public DateTime? ScheduledFor { get; set; }
    public int Version { get; set; }

    [JsonIgnore]
    public bool IsPublished => Status == PostStatuses.Published;
}

public record PostHeader(int Id, string? AuthorId, bool IsPublished);
