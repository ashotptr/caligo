namespace Caligo.Catalog.Application.Music;

public record LibraryTrack(
    string AudioUri,
    string Title,
    string Artist,
    float DurationMinutes,
    DateOnly? ReleaseDate,
    DateOnly AddedOn,
    int? TrackNumber,
    string? Label,
    IReadOnlyList<string> Genres);

public record LibraryRelease(
    string Title,
    string Artist,
    string? CoverUri,
    bool IsSingle,
    IReadOnlyList<LibraryTrack> Tracks);

public record MusicLibrary(IReadOnlyList<LibraryRelease> Releases, IReadOnlyList<string> Problems);

public record MusicImportResult(
    int Releases,
    int Tracks,
    int ArtistsAdded,
    int AlbumsAdded,
    int SinglesAdded,
    int SongsAdded,
    int SongsAlreadyThere,
    IReadOnlyList<string> Problems);

public interface IMusicLibrary
{
    bool IsConfigured { get; }

    Task<MusicLibrary> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IMusicImportStore
{
    Task<MusicImportResult> ImportAsync(MusicLibrary library, CancellationToken cancellationToken = default);
}

public class MusicImportService
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly IMusicLibrary _library;
    private readonly IMusicImportStore _store;

    public MusicImportService(IMusicLibrary library, IMusicImportStore store)
    {
        _library = library;
        _store = store;
    }

    public bool IsConfigured => _library.IsConfigured;

    public async Task<MusicImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        await OneAtATime.WaitAsync(cancellationToken);

        try
        {
            var library = await _library.ReadAsync(cancellationToken);

            return await _store.ImportAsync(library, cancellationToken);
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
