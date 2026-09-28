namespace Blog.Posts.Application.Posts;

public class PostingRules
{
    public const string SectionName = "Posts";

    public int MaxScheduledPerAuthor { get; set; } = 25;

    public TimeSpan MaxScheduleAhead { get; set; } = TimeSpan.FromDays(365);
}
