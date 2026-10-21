using Caligo.Catalog.Application.Collections;
using Caligo.Catalog.Application.Contents;

namespace Caligo.Catalog.Application.Abstractions;

public interface ICollectionStore
{
    Task<IReadOnlyList<CollectionSummaryDto>> ListAsync(
        string identityId, HiddenMusic hidden, CancellationToken cancellationToken = default);

    Task<CollectionDetailDto?> GetAsync(
        int collectionId, string identityId, HiddenMusic hidden, CancellationToken cancellationToken = default);

    Task<bool> OwnsAsync(int collectionId, string identityId, CancellationToken cancellationToken = default);

    Task<bool> NameTakenAsync(
        string identityId, string name, int? exceptCollectionId, CancellationToken cancellationToken = default);

    Task<CollectionSummaryDto?> CreateAsync(
        string identityId, string name, DateTime createdAt, CancellationToken cancellationToken = default);

    Task<bool> RenameAsync(int collectionId, string name, CancellationToken cancellationToken = default);

    Task DeleteAsync(int collectionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> MissingContentAsync(
        IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default);

    Task<AddTitlesResult> AddAsync(
        int collectionId, IReadOnlyList<int> contentIds, DateTime addedAt, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(int collectionId, int contentId, CancellationToken cancellationToken = default);
}
