using Caligo.Catalog.Application.Engine;

namespace Caligo.Catalog.Application.Abstractions;

public interface ICaligoEngine
{
    bool IsConfigured { get; }

    Task<IReadOnlySet<string>?> VisibleMixtapeFoldersAsync(string? user, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> RequestSongAsync(string user, string query, CancellationToken cancellationToken = default);

    Task<RadioQueueResult> QueueToRadioAsync(RadioQueueRequest request, CancellationToken cancellationToken = default);

    Task<AskAnswer?> AskAsync(string sessionId, string query, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> AddToRotationAsync(RotationRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<CaligoTrack>>> GetPlaylistsAsync(
        string user, CancellationToken cancellationToken = default);
}
