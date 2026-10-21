namespace Caligo.Catalog.Domain.Collections;

public class Collection
{
    public const int MaxNameLength = 100;

    public int CollectionId { get; set; }

    public string IdentityId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();

        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength ? null : trimmed;
    }
}

public class CollectionTitle
{
    public int CollectionId { get; set; }

    public int ContentId { get; set; }

    public int Position { get; set; }

    public DateTime AddedAt { get; set; }
}
