namespace Caligo.SharedKernel;

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();

    public string? NextCursor { get; set; }

    public bool HasMore => NextCursor is not null;
}
