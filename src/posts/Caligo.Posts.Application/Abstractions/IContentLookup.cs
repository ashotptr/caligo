namespace Caligo.Posts.Application.Abstractions;

public interface IContentLookup
{
    Task<bool> ExistsAsync(int contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, string>> GetTitlesAsync(
        IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default);
}
