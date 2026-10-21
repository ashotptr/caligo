using Caligo.Catalog.Application.Engine;

namespace Caligo.Catalog.Application.Abstractions;

public interface IMixtapeLookup
{
    Task<MixtapeTrack?> TrackAsync(int contentId, CancellationToken cancellationToken = default);

    Task<string?> AlbumFolderAsync(int albumId, CancellationToken cancellationToken = default);

    Task<string?> SingleFolderAsync(int singleId, CancellationToken cancellationToken = default);
}
