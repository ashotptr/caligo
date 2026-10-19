namespace Caligo.Posts.Domain.Posts;

public static class PostTags
{
    public const int MaxTagsPerPost = 10;

    public const int MaxTagLength = 40;

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return Array.Empty<string>();
        }

        return tags
            .Where(tag => tag is not null)
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Select(tag => tag.Length <= MaxTagLength ? tag : tag[..MaxTagLength])
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTagsPerPost)
            .ToList();
    }
}
