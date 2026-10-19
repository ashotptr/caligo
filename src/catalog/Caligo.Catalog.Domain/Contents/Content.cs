namespace Caligo.Catalog.Domain.Contents;

public class Content
{
    public const int MaxTitleLength = 50;

    public int ContentId { get; set; }

    public string? Description { get; set; }

    public string Title { get; set; } = string.Empty;

    public float Duration { get; set; }

    public DateOnly ReleaseDate { get; set; }

    public int ProductionCompanyId { get; set; }

    public float? AverageRating { get; set; }

    public static string FitTitle(string title)
    {
        var trimmed = title.Trim();

        return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
    }
}

public class Movie
{
    public int MovieId { get; set; }

    public int ContentId { get; set; }

    public float? Budget { get; set; }

    public float? BoxOffice { get; set; }
}

public class Series
{
    public int SeriesId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateOnly ReleaseDate { get; set; }
}

public class Season
{
    public int SeriesId { get; set; }

    public int SeasonNumber { get; set; }

    public string? Name { get; set; }
}

public class Episode
{
    public int SeriesId { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public int ContentId { get; set; }
}

public class Subtitle
{
    public int SubtitleId { get; set; }

    public int ContentId { get; set; }

    public int LanguageId { get; set; }
}
