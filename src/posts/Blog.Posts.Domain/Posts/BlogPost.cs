using Blog.Posts.Domain.Events;

namespace Blog.Posts.Domain.Posts;

public enum PostStatus
{
    Published = 0,

    Scheduled = 1
}

public class BlogPost
{
    private readonly List<Func<BlogPost, IDomainEvent>> _eventFactories = new();

    private List<string> _tags = new();

    private BlogPost()
    {
    }

    public int Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Content { get; private set; } = string.Empty;

    public DateTime PublishedDate { get; private set; }

    public string? AuthorId { get; private set; }

    public IReadOnlyList<string> Tags => _tags;

    public PostStatus Status { get; private set; }

    public DateTime? ScheduledFor { get; private set; }

    // Optimistic concurrency token: every change bumps it, and the UPDATE only matches the row
    // if nobody else bumped it since this copy was loaded.
    public int Version { get; private set; }

    public bool IsPublished => Status == PostStatus.Published;

    public bool HasPendingEvents => _eventFactories.Count > 0;

    public static BlogPost Publish(string title, string content, IEnumerable<string>? tags, string? authorId, DateTime publishedAt)
    {
        var post = new BlogPost
        {
            Title = Require(title, nameof(title)),
            Content = Require(content, nameof(content)),
            AuthorId = authorId,
            PublishedDate = publishedAt,
            Status = PostStatus.Published,
            _tags = PostTags.Normalize(tags).ToList()
        };

        post._eventFactories.Add(static p => new PostPublished(p.Id, p.Title, p.AuthorId, p.Tags));

        return post;
    }

    // Nothing is announced until the post actually goes live, so a scheduled post raises no event.
    public static BlogPost Schedule(string title, string content, IEnumerable<string>? tags, string? authorId, DateTime publishAt) =>
        new()
        {
            Title = Require(title, nameof(title)),
            Content = Require(content, nameof(content)),
            AuthorId = authorId,
            PublishedDate = publishAt,
            ScheduledFor = publishAt,
            Status = PostStatus.Scheduled,
            _tags = PostTags.Normalize(tags).ToList()
        };

    public void Revise(string title, string content, IEnumerable<string>? tags)
    {
        Title = Require(title, nameof(title));
        Content = Require(content, nameof(content));
        _tags = PostTags.Normalize(tags).ToList();

        Version++;

        if (IsPublished)
        {
            _eventFactories.Add(static p => new PostUpdated(p.Id, p.Title));
        }
    }

    public void Reschedule(DateTime publishAt)
    {
        if (IsPublished)
        {
            throw new InvalidOperationException("A published post cannot be rescheduled.");
        }

        ScheduledFor = publishAt;
        PublishedDate = publishAt;

        Version++;
    }

    // Returns false when the post is not due, so a publisher that claimed it can skip it safely.
    public bool PublishDue(DateTime now)
    {
        if (IsPublished || ScheduledFor > now)
        {
            return false;
        }

        Status = PostStatus.Published;
        PublishedDate = now;

        Version++;

        _eventFactories.Add(static p => new PostPublished(p.Id, p.Title, p.AuthorId, p.Tags));

        return true;
    }

    public void MarkDeleted()
    {
        if (IsPublished)
        {
            _eventFactories.Add(static p => new PostDeleted(p.Id));
        }
    }

    public IReadOnlyList<IDomainEvent> DrainDomainEvents()
    {
        if (_eventFactories.Count == 0)
        {
            return Array.Empty<IDomainEvent>();
        }

        var events = _eventFactories.Select(factory => factory(this)).ToList();

        _eventFactories.Clear();

        return events;
    }

    public bool CanBeModifiedBy(string? userId, bool isAdministrator) =>
        isAdministrator || (userId is not null && AuthorId == userId);

    public string BuildExcerpt() => Excerpt.From(Content);

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value;
}
