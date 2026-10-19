using Caligo.Catalog.Application.Abstractions;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Contents;

public class CatalogService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int SimilarCount = 10;
    public const int MaxSearchLength = 100;
    public const int MaxLookupTitleLength = 300;

    private readonly ICatalogReader _catalog;

    public CatalogService(ICatalogReader catalog)
    {
        _catalog = catalog;
    }

    public async Task<Result<PagedResult<ContentSummaryDto>>> GetPageAsync(
        string? kind, int? genreId, string? search, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !ContentKinds.IsKnown(kind))
        {
            return Result<PagedResult<ContentSummaryDto>>.Invalid(
                "Kind must be one of: movie, episode, song, other.");
        }

        var page = await _catalog.GetContentPageAsync(
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            genreId,
            Normalize(search),
            ContentCursor.TryDecode(cursor),
            Math.Clamp(limit, 1, MaxPageSize),
            cancellationToken);

        return Result<PagedResult<ContentSummaryDto>>.Ok(page);
    }

    public async Task<Result<ContentDetailDto>> GetAsync(int contentId, CancellationToken cancellationToken = default)
    {
        var content = await _catalog.GetContentAsync(contentId, cancellationToken);

        return content is null
            ? Result<ContentDetailDto>.NotFound()
            : Result<ContentDetailDto>.Ok(content);
    }

    public async Task<Result<IReadOnlyList<SimilarContentDto>>> GetSimilarAsync(
        int contentId, CancellationToken cancellationToken = default)
    {
        if (!await _catalog.ContentExistsAsync(contentId, cancellationToken))
        {
            return Result<IReadOnlyList<SimilarContentDto>>.NotFound();
        }

        return Result<IReadOnlyList<SimilarContentDto>>.Ok(
            await _catalog.GetSimilarAsync(contentId, SimilarCount, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<CommentDto>>> GetCommentsAsync(
        int contentId, int? beforeCommentId, int limit, CancellationToken cancellationToken = default)
    {
        if (!await _catalog.ContentExistsAsync(contentId, cancellationToken))
        {
            return Result<IReadOnlyList<CommentDto>>.NotFound();
        }

        return Result<IReadOnlyList<CommentDto>>.Ok(await _catalog.GetCommentsAsync(
            contentId, beforeCommentId, Math.Clamp(limit, 1, MaxPageSize), cancellationToken));
    }

    public Task<IReadOnlyList<SeriesSummaryDto>> GetSeriesListAsync(CancellationToken cancellationToken = default) =>
        _catalog.GetSeriesListAsync(cancellationToken);

    public Task<Result<SeriesDetailDto>> GetSeriesAsync(int seriesId, CancellationToken cancellationToken = default) =>
        Found(_catalog.GetSeriesAsync(seriesId, cancellationToken));

    public Task<Result<ActorDetailDto>> GetActorAsync(int actorId, CancellationToken cancellationToken = default) =>
        Found(_catalog.GetActorAsync(actorId, cancellationToken));

    public Task<IReadOnlyList<ArtistSummaryDto>> GetArtistsAsync(
        string? search, int limit, CancellationToken cancellationToken = default) =>
        _catalog.GetArtistsAsync(Normalize(search), Math.Clamp(limit, 1, MaxPageSize), cancellationToken);

    public Task<Result<ArtistDetailDto>> GetArtistAsync(int artistId, CancellationToken cancellationToken = default) =>
        Found(_catalog.GetArtistAsync(artistId, cancellationToken));

    public Task<IReadOnlyList<AlbumSummaryDto>> GetAlbumsAsync(
        string? search, int limit, CancellationToken cancellationToken = default) =>
        _catalog.GetAlbumsAsync(Normalize(search), Math.Clamp(limit, 1, MaxPageSize), cancellationToken);

    public Task<Result<AlbumDetailDto>> GetAlbumAsync(int albumId, CancellationToken cancellationToken = default) =>
        Found(_catalog.GetAlbumAsync(albumId, cancellationToken));

    public Task<Result<SingleDetailDto>> GetSingleAsync(int singleId, CancellationToken cancellationToken = default) =>
        Found(_catalog.GetSingleAsync(singleId, cancellationToken));

    public async Task<Result<SongMatchDto>> FindSongAsync(
        string? artist, string? title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) ||
            artist.Length > MaxSearchLength || title.Length > MaxLookupTitleLength)
        {
            return Result<SongMatchDto>.Invalid("Give the artist and the title of the song.");
        }

        var match = await _catalog.FindSongAsync(artist.Trim(), title.Trim(), cancellationToken);

        return match is null ? Result<SongMatchDto>.NotFound() : Result<SongMatchDto>.Ok(match);
    }

    public Task<IReadOnlyList<NamedDto>> GetGenresAsync(CancellationToken cancellationToken = default) =>
        _catalog.GetGenresAsync(cancellationToken);

    public Task<IReadOnlyList<NamedDto>> GetLanguagesAsync(CancellationToken cancellationToken = default) =>
        _catalog.GetLanguagesAsync(cancellationToken);

    private static string? Normalize(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var trimmed = search.Trim();

        return trimmed.Length <= MaxSearchLength ? trimmed : trimmed[..MaxSearchLength];
    }

    private static async Task<Result<T>> Found<T>(Task<T?> lookup) where T : class
    {
        var value = await lookup;

        return value is null ? Result<T>.NotFound() : Result<T>.Ok(value);
    }
}
