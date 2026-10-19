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

    public int Version { get; private set; }

    public int? ContentId { get; private set; }

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

        post._eventFactories.Add(static p => new PostPublished(p.Id, p.Title, p.AuthorId, p.Tags) { ContentId = p.ContentId });

        return post;
    }

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

    public void LinkToContent(int? contentId)
    {
        if (contentId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentId), "A content id must be positive.");
        }

        ContentId = contentId;
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

    public bool PublishDue(DateTime now)
    {
        if (IsPublished || ScheduledFor > now)
        {
            return false;
        }

        Status = PostStatus.Published;
        PublishedDate = now;

        Version++;

        _eventFactories.Add(static p => new PostPublished(p.Id, p.Title, p.AuthorId, p.Tags) { ContentId = p.ContentId });

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
