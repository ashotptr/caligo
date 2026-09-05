using Blog.Posts.Domain.Events;

namespace Blog.Posts.Domain.Posts;

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

    public bool HasPendingEvents => _eventFactories.Count > 0;

    public static BlogPost Publish(string title, string content, IEnumerable<string>? tags, string? authorId, DateTime publishedAt)
    {
        var post = new BlogPost
        {
            Title = Require(title, nameof(title)),
            Content = Require(content, nameof(content)),
            AuthorId = authorId,
            PublishedDate = publishedAt,
            _tags = PostTags.Normalize(tags).ToList()
        };

        post._eventFactories.Add(static p => new PostPublished(p.Id, p.Title, p.AuthorId, p.Tags));

        return post;
    }

    public void Revise(string title, string content, IEnumerable<string>? tags)
    {
        Title = Require(title, nameof(title));
        Content = Require(content, nameof(content));
        _tags = PostTags.Normalize(tags).ToList();

        _eventFactories.Add(static p => new PostUpdated(p.Id, p.Title));
    }

    public void MarkDeleted() => _eventFactories.Add(static p => new PostDeleted(p.Id));

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
