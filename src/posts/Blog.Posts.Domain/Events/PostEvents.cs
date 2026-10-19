namespace Blog.Posts.Domain.Events;

public abstract record PostEvent(int PostId) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();

    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}

public sealed record PostPublished(int PostId, string Title, string? AuthorId, IReadOnlyList<string> Tags)
    : PostEvent(PostId)
{
    public int? ContentId { get; init; }
}

public sealed record PostUpdated(int PostId, string Title) : PostEvent(PostId);

public sealed record PostDeleted(int PostId) : PostEvent(PostId);
