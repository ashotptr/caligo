using Blog.Catalog.Application.Contents;
using Blog.SharedKernel;

namespace Blog.Catalog.Application.Abstractions;

public interface ICatalogReader
{
    Task<PagedResult<ContentSummaryDto>> GetContentPageAsync(
        string? kind, int? genreId, string? search, ContentCursor? cursor, int limit,
        CancellationToken cancellationToken = default);

    Task<ContentDetailDto?> GetContentAsync(int contentId, CancellationToken cancellationToken = default);

    Task<bool> ContentExistsAsync(int contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, string>> GetTitlesAsync(
        IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarContentDto>> GetSimilarAsync(
        int contentId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommentDto>> GetCommentsAsync(
        int contentId, int? beforeCommentId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeriesSummaryDto>> GetSeriesListAsync(CancellationToken cancellationToken = default);

    Task<SeriesDetailDto?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken = default);

    Task<ActorDetailDto?> GetActorAsync(int actorId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtistSummaryDto>> GetArtistsAsync(
        string? search, int limit, CancellationToken cancellationToken = default);

    Task<ArtistDetailDto?> GetArtistAsync(int artistId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlbumSummaryDto>> GetAlbumsAsync(
        string? search, int limit, CancellationToken cancellationToken = default);

    Task<AlbumDetailDto?> GetAlbumAsync(int albumId, CancellationToken cancellationToken = default);

    Task<SingleDetailDto?> GetSingleAsync(int singleId, CancellationToken cancellationToken = default);

    Task<SongMatchDto?> FindSongAsync(string artist, string title, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NamedDto>> GetGenresAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NamedDto>> GetLanguagesAsync(CancellationToken cancellationToken = default);
}
