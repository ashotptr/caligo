namespace Blog.Catalog.Application.Contents;

public static class ContentKinds
{
    public const string Movie = "movie";
    public const string Episode = "episode";
    public const string Song = "song";
    public const string Other = "other";

    public static bool IsKnown(string? kind) => kind is Movie or Episode or Song or Other;
}

public record NamedDto(int Id, string Name);

public record ContentQuery(string? Kind, int? GenreId, string? Search, string? Cursor, int Limit);

public class ContentSummaryDto
{
    public int ContentId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Kind { get; init; } = ContentKinds.Other;
    public float Duration { get; init; }
    public DateOnly ReleaseDate { get; init; }
    public float? AverageRating { get; init; }
    public List<string> Genres { get; init; } = new();
}

public record MovieDto(int MovieId, float? Budget, float? BoxOffice);

public record EpisodeDto(int SeriesId, string SeriesTitle, int SeasonNumber, string? SeasonName, int EpisodeNumber);

public record SongDto(
    int SongId,
    string? AudioUri,
    string? Lyrics,
    List<SongArtistDto> Artists,
    List<SongReleaseDto> Albums,
    List<SongReleaseDto> Singles);

public record SongArtistDto(int ArtistId, string Name, string? Role);

public record SongReleaseDto(int Id, string Title, int? TrackNumber);

public record ContentActorDto(int ActorId, string Name);

public class ContentDetailDto
{
    public int ContentId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Kind { get; init; } = ContentKinds.Other;
    public float Duration { get; init; }
    public DateOnly ReleaseDate { get; init; }
    public float? AverageRating { get; init; }
    public NamedDto ProductionCompany { get; init; } = new(0, string.Empty);
    public List<NamedDto> DistributingCompanies { get; init; } = new();
    public List<NamedDto> Genres { get; init; } = new();
    public List<NamedDto> Languages { get; init; } = new();
    public List<NamedDto> Subtitles { get; init; } = new();
    public List<ContentActorDto> Actors { get; init; } = new();
    public MovieDto? Movie { get; init; }
    public EpisodeDto? Episode { get; init; }
    public SongDto? Song { get; init; }
    public int Views { get; init; }
    public int Interactions { get; init; }
    public int Comments { get; init; }
}

public record SimilarContentDto(int ContentId, string Title, string Kind, float Similarity);

public record SeriesSummaryDto(int SeriesId, string Title, DateOnly ReleaseDate, int Seasons);

public record SeriesEpisodeDto(int EpisodeNumber, int ContentId, string Title, float Duration, DateOnly ReleaseDate);

public record SeasonDto(int SeasonNumber, string? Name, List<SeriesEpisodeDto> Episodes);

public record SeriesDetailDto(int SeriesId, string Title, string Description, DateOnly ReleaseDate, List<SeasonDto> Seasons);

public record ActorDetailDto(
    int ActorId,
    string Name,
    DateOnly Dob,
    string Nationality,
    string Biography,
    string Gender,
    string? ProfilePictureUri,
    List<ContentSummaryDto> Contents);

public record ArtistSummaryDto(int ArtistId, string Name, int Songs);

public record AlbumSummaryDto(int AlbumId, string Title, DateOnly? ReleaseDate, string? CoverUri, List<string> Artists, int Songs);

public record TrackDto(int ContentId, int SongId, string Title, float Duration, int? DiscNumber, int? TrackNumber, string? AudioUri, List<string> Artists);

public record ArtistDetailDto(
    int ArtistId,
    string Name,
    string? Country,
    DateOnly? FormedDate,
    string? Biography,
    string? PictureUri,
    List<AlbumSummaryDto> Albums,
    List<NamedDto> Singles,
    List<TrackDto> Songs);

public record AlbumDetailDto(
    int AlbumId,
    string Title,
    string? Description,
    DateOnly? ReleaseDate,
    string? CoverUri,
    List<NamedDto> Artists,
    List<TrackDto> Tracks);

public record SingleDetailDto(
    int SingleId,
    string Title,
    DateOnly? ReleaseDate,
    string? CoverUri,
    List<NamedDto> Artists,
    List<TrackDto> Tracks);

public record CommentDto(int CommentId, int ContentId, string Author, string Comment);

public record SongMatchDto(int ContentId, int SongId, string Title, string Artist);
