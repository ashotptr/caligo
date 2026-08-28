namespace Blog.Posts.Domain.Posts;

/// <summary>
/// Normalisation rules for post tags. These are domain rules, not input validation: two posts
/// tagged "C#" and " c# " carry the same tag, and that has to be true wherever a tag enters
/// the system, not just at the one controller that happened to call the old private helper.
/// </summary>
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
