namespace Blog.Api.Analytics
{
    public class PostViewedEvent
    {
        public int PostId { get; set; }

        public DateTime ViewedAt { get; set; } = DateTime.UtcNow;

        public string? Referrer { get; set; }

        public string? UserId { get; set; }

        public string? VisitorHash { get; set; }
    }
}
