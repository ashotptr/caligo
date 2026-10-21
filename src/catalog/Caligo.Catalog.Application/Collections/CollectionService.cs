using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Collections;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Collections;

public class CollectionService
{
    public const int MaxTitlesPerAdd = 500;

    private readonly ICollectionStore _store;
    private readonly ICatalogViewer _viewer;
    private readonly TimeProvider _time;

    public CollectionService(ICollectionStore store, ICatalogViewer viewer, TimeProvider time)
    {
        _store = store;
        _viewer = viewer;
        _time = time;
    }

    public async Task<Result<IReadOnlyList<CollectionSummaryDto>>> ListAsync(
        Caller caller, CancellationToken cancellationToken = default)
    {
        if (caller.UserId is not { } owner)
        {
            return Result<IReadOnlyList<CollectionSummaryDto>>.Unauthorized();
        }

        var hidden = await _viewer.HiddenAsync(cancellationToken);

        return Result<IReadOnlyList<CollectionSummaryDto>>.Ok(await _store.ListAsync(owner, hidden, cancellationToken));
    }

    public async Task<Result<CollectionDetailDto>> GetAsync(
        int collectionId, Caller caller, CancellationToken cancellationToken = default)
    {
        if (caller.UserId is not { } owner)
        {
            return Result<CollectionDetailDto>.Unauthorized();
        }

        var hidden = await _viewer.HiddenAsync(cancellationToken);
        var collection = await _store.GetAsync(collectionId, owner, hidden, cancellationToken);

        return collection is null ? Result<CollectionDetailDto>.NotFound() : Result<CollectionDetailDto>.Ok(collection);
    }

    public async Task<Result<CollectionSummaryDto>> CreateAsync(
        CollectionNameCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (caller.UserId is not { } owner)
        {
            return Result<CollectionSummaryDto>.Unauthorized();
        }

        if (Collection.CleanName(command.Name) is not { } name)
        {
            return Result<CollectionSummaryDto>.Invalid(
                $"Give the collection a name of 1 to {Collection.MaxNameLength} characters.");
        }

        if (await _store.NameTakenAsync(owner, name, null, cancellationToken))
        {
            return Result<CollectionSummaryDto>.Conflict($"You already have a collection called {name}.");
        }

        var created = await _store.CreateAsync(owner, name, Now(), cancellationToken);

        return created is null
            ? Result<CollectionSummaryDto>.Conflict($"You already have a collection called {name}.")
            : Result<CollectionSummaryDto>.Ok(created);
    }

    public async Task<Result> RenameAsync(
        int collectionId, CollectionNameCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        var owned = await OwnedAsync(collectionId, caller, cancellationToken);

        if (owned.Status != ResultStatus.Ok)
        {
            return owned;
        }

        if (Collection.CleanName(command.Name) is not { } name)
        {
            return Result.Invalid($"Give the collection a name of 1 to {Collection.MaxNameLength} characters.");
        }

        if (await _store.NameTakenAsync(caller.UserId!, name, collectionId, cancellationToken)
            || !await _store.RenameAsync(collectionId, name, cancellationToken))
        {
            return Result.Conflict($"You already have a collection called {name}.");
        }

        return Result.Ok();
    }

    public async Task<Result> DeleteAsync(int collectionId, Caller caller, CancellationToken cancellationToken = default)
    {
        var owned = await OwnedAsync(collectionId, caller, cancellationToken);

        if (owned.Status != ResultStatus.Ok)
        {
            return owned;
        }

        await _store.DeleteAsync(collectionId, cancellationToken);

        return Result.Ok();
    }

    public async Task<Result<AddTitlesResult>> AddAsync(
        int collectionId, AddTitlesCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        var contentIds = (command.ContentIds ?? Array.Empty<int>()).Distinct().ToList();

        if (contentIds.Count == 0 || contentIds.Count > MaxTitlesPerAdd)
        {
            return Result<AddTitlesResult>.Invalid($"Add between 1 and {MaxTitlesPerAdd} titles at a time.");
        }

        var owned = await OwnedAsync(collectionId, caller, cancellationToken);

        if (owned.Status != ResultStatus.Ok)
        {
            return new Result<AddTitlesResult>(owned.Status, default, owned.Error);
        }

        var hidden = await _viewer.HiddenAsync(cancellationToken);
        var missing = await _store.MissingContentAsync(contentIds, cancellationToken);

        if (missing.Count > 0 || contentIds.Any(id => hidden.ContentIds.Contains(id)))
        {
            return Result<AddTitlesResult>.NotFound("Some of those titles don't exist.");
        }

        return Result<AddTitlesResult>.Ok(
            await _store.AddAsync(collectionId, contentIds, Now(), cancellationToken));
    }

    public async Task<Result> RemoveAsync(
        int collectionId, int contentId, Caller caller, CancellationToken cancellationToken = default)
    {
        var owned = await OwnedAsync(collectionId, caller, cancellationToken);

        if (owned.Status != ResultStatus.Ok)
        {
            return owned;
        }

        return await _store.RemoveAsync(collectionId, contentId, cancellationToken) ? Result.Ok() : Result.NotFound();
    }

    private async Task<Result> OwnedAsync(int collectionId, Caller caller, CancellationToken cancellationToken)
    {
        if (caller.UserId is not { } owner)
        {
            return Result.Unauthorized();
        }

        return await _store.OwnsAsync(collectionId, owner, cancellationToken) ? Result.Ok() : Result.NotFound();
    }

    private DateTime Now() => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
