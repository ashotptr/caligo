namespace Blog.Posts.Application.Posts;

/// <summary>
/// A keyset ("seek") page. Unlike OFFSET paging, the cost of fetching page N does not
/// grow with N, and rows cannot be skipped or duplicated when posts are written while a
/// reader is paging through.
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();

    /// <summary>Opaque cursor for the next page, or null when the last page has been reached.</summary>
    public string? NextCursor { get; set; }

    public bool HasMore => NextCursor is not null;
}
