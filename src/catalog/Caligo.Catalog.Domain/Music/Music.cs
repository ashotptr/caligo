namespace Caligo.Catalog.Domain.Music;

public class Artist
{
    public const int MaxNameLength = 100;

    public int ArtistId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Country { get; set; }

    public DateOnly? FormedDate { get; set; }

    public string? Biography { get; set; }

    public string? PictureUri { get; set; }
}

public class Album
{
    public const int MaxTitleLength = 100;

    public int AlbumId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    public string? CoverUri { get; set; }
}

public class SingleRelease
{
    public const int MaxTitleLength = 100;

    public int SingleId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateOnly? ReleaseDate { get; set; }

    public string? CoverUri { get; set; }
}

public class Song
{
    public int SongId { get; set; }

    public int ContentId { get; set; }

    public string? AudioUri { get; set; }

    public string? Lyrics { get; set; }
}

public class SongArtist
{
    public const string PrimaryRole = "primary";

    public int SongId { get; set; }

    public int ArtistId { get; set; }

    public string? Role { get; set; }
}

public class AlbumArtist
{
    public int AlbumId { get; set; }

    public int ArtistId { get; set; }
}

public class AlbumSong
{
    public int AlbumId { get; set; }

    public int SongId { get; set; }

    public int DiscNumber { get; set; } = 1;

    public int? TrackNumber { get; set; }
}

public class SingleArtist
{
    public int SingleId { get; set; }

    public int ArtistId { get; set; }
}

public class SingleSong
{
    public int SingleId { get; set; }

    public int SongId { get; set; }

    public int? TrackNumber { get; set; }
}
