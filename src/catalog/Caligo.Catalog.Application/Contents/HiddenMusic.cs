namespace Caligo.Catalog.Application.Contents;

public sealed record HiddenMusic(
    IReadOnlyList<int> ContentIds,
    IReadOnlyList<int> SongIds,
    IReadOnlyList<int> AlbumIds,
    IReadOnlyList<int> SingleIds)
{
    public static readonly HiddenMusic None = new(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

    public bool IsEmpty => ContentIds.Count == 0 && SongIds.Count == 0 && AlbumIds.Count == 0 && SingleIds.Count == 0;
}
