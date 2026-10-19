using Microsoft.EntityFrameworkCore;
using Blog.Catalog.Application.Music;
using Blog.Catalog.Domain.Contents;
using Blog.Catalog.Domain.Music;
using Blog.Catalog.Infrastructure.Persistence;

namespace Blog.Catalog.Infrastructure.Music
{
    public class MusicImportStore : IMusicImportStore
    {
        public const string UnknownLabel = "Unknown label";

        private readonly StreamingDbContext _db;

        public MusicImportStore(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<MusicImportResult> ImportAsync(MusicLibrary library, CancellationToken cancellationToken = default)
        {
            var counts = new Counts();

            foreach (var release in library.Releases)
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

                await ImportReleaseAsync(release, counts, cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                _db.ChangeTracker.Clear();
            }

            return new MusicImportResult(
                library.Releases.Count,
                library.Releases.Sum(r => r.Tracks.Count),
                counts.Artists,
                counts.Albums,
                counts.Singles,
                counts.Songs,
                counts.Existing,
                library.Problems);
        }

        private async Task ImportReleaseAsync(LibraryRelease release, Counts counts, CancellationToken cancellationToken)
        {
            var artist = await ArtistAsync(release.Artist, counts, cancellationToken);

            var releaseDate = release.Tracks.Min(t => t.ReleaseDate);
            var numbers = TrackNumbers(release.Tracks);

            Album? album = null;
            SingleRelease? single = null;

            if (release.IsSingle)
            {
                single = await SingleAsync(release, artist, releaseDate, counts, cancellationToken);
            }
            else
            {
                album = await AlbumAsync(release, artist, releaseDate, counts, cancellationToken);
            }

            for (var index = 0; index < release.Tracks.Count; index++)
            {
                var track = release.Tracks[index];
                var title = Content.FitTitle(track.Title);

                var existing = await ExistingSongAsync(track.AudioUri, title, artist.ArtistId, cancellationToken);

                if (existing is not null)
                {
                    counts.Existing++;

                    await LinkAsync(existing.Value, album, single, numbers[index], cancellationToken);

                    continue;
                }

                var content = new Content
                {
                    Title = title,
                    Duration = track.DurationMinutes,
                    ReleaseDate = track.ReleaseDate ?? releaseDate ?? track.AddedOn,
                    ProductionCompanyId = await LabelAsync(track.Label, cancellationToken),
                    AverageRating = 0
                };

                _db.Contents.Add(content);

                await _db.SaveChangesAsync(cancellationToken);

                var song = new Song { ContentId = content.ContentId, AudioUri = track.AudioUri };

                _db.Songs.Add(song);

                await _db.SaveChangesAsync(cancellationToken);

                _db.SongArtists.Add(new SongArtist
                {
                    SongId = song.SongId,
                    ArtistId = artist.ArtistId,
                    Role = SongArtist.PrimaryRole
                });

                foreach (var genreId in await GenresAsync(track.Genres, cancellationToken))
                {
                    _db.ContentGenres.Add(new ContentGenre { ContentId = content.ContentId, GenreId = genreId });
                }

                await _db.SaveChangesAsync(cancellationToken);
                await LinkAsync(song.SongId, album, single, numbers[index], cancellationToken);

                counts.Songs++;
            }
        }

        private static IReadOnlyList<int> TrackNumbers(IReadOnlyList<LibraryTrack> tracks)
        {
            var tagged = tracks.Select(t => t.TrackNumber).ToList();

            return tagged.All(number => number is not null) && tagged.Distinct().Count() == tagged.Count
                ? tagged.Select(number => number!.Value).ToList()
                : Enumerable.Range(1, tracks.Count).ToList();
        }

        private async Task<Artist> ArtistAsync(string name, Counts counts, CancellationToken cancellationToken)
        {
            var fitted = Fit(name, Artist.MaxNameLength);
            var pattern = ContentRows.EscapeLike(fitted);

            var artist = await _db.Artists
                .Where(a => EF.Functions.ILike(a.Name, pattern, "\\"))
                .OrderBy(a => a.ArtistId)
                .FirstOrDefaultAsync(cancellationToken);

            if (artist is not null)
            {
                return artist;
            }

            artist = new Artist { Name = fitted };

            _db.Artists.Add(artist);

            await _db.SaveChangesAsync(cancellationToken);

            counts.Artists++;

            return artist;
        }

        private async Task<Album> AlbumAsync(
            LibraryRelease release, Artist artist, DateOnly? releaseDate, Counts counts, CancellationToken cancellationToken)
        {
            var title = Fit(release.Title, Album.MaxTitleLength);
            var pattern = ContentRows.EscapeLike(title);

            var album = await (
                from a in _db.Albums
                join link in _db.AlbumArtists on a.AlbumId equals link.AlbumId
                where link.ArtistId == artist.ArtistId && EF.Functions.ILike(a.Title, pattern, "\\")
                orderby a.AlbumId
                select a).FirstOrDefaultAsync(cancellationToken);

            if (album is not null)
            {
                if (album.CoverUri is null && release.CoverUri is not null)
                {
                    album.CoverUri = release.CoverUri;

                    await _db.SaveChangesAsync(cancellationToken);
                }

                return album;
            }

            album = new Album { Title = title, ReleaseDate = releaseDate, CoverUri = release.CoverUri };

            _db.Albums.Add(album);

            await _db.SaveChangesAsync(cancellationToken);

            _db.AlbumArtists.Add(new AlbumArtist { AlbumId = album.AlbumId, ArtistId = artist.ArtistId });

            await _db.SaveChangesAsync(cancellationToken);

            counts.Albums++;

            return album;
        }

        private async Task<SingleRelease> SingleAsync(
            LibraryRelease release, Artist artist, DateOnly? releaseDate, Counts counts, CancellationToken cancellationToken)
        {
            var title = Fit(release.Title, SingleRelease.MaxTitleLength);
            var pattern = ContentRows.EscapeLike(title);

            var single = await (
                from s in _db.Singles
                join link in _db.SingleArtists on s.SingleId equals link.SingleId
                where link.ArtistId == artist.ArtistId && EF.Functions.ILike(s.Title, pattern, "\\")
                orderby s.SingleId
                select s).FirstOrDefaultAsync(cancellationToken);

            if (single is not null)
            {
                return single;
            }

            single = new SingleRelease { Title = title, ReleaseDate = releaseDate, CoverUri = release.CoverUri };

            _db.Singles.Add(single);

            await _db.SaveChangesAsync(cancellationToken);

            _db.SingleArtists.Add(new SingleArtist { SingleId = single.SingleId, ArtistId = artist.ArtistId });

            await _db.SaveChangesAsync(cancellationToken);

            counts.Singles++;

            return single;
        }

        private async Task<int?> ExistingSongAsync(
            string audioUri, string title, int artistId, CancellationToken cancellationToken)
        {
            var byFile = await _db.Songs.AsNoTracking()
                .Where(s => s.AudioUri == audioUri)
                .OrderBy(s => s.SongId)
                .Select(s => (int?)s.SongId)
                .FirstOrDefaultAsync(cancellationToken);

            if (byFile is not null)
            {
                return byFile;
            }

            var pattern = ContentRows.EscapeLike(title);

            return await (
                from s in _db.Songs.AsNoTracking()
                join c in _db.Contents on s.ContentId equals c.ContentId
                join link in _db.SongArtists on s.SongId equals link.SongId
                where link.ArtistId == artistId && EF.Functions.ILike(c.Title, pattern, "\\")
                orderby s.SongId
                select (int?)s.SongId).FirstOrDefaultAsync(cancellationToken);
        }

        private async Task LinkAsync(int songId, Album? album, SingleRelease? single, int trackNumber, CancellationToken cancellationToken)
        {
            if (album is not null &&
                !await _db.AlbumSongs.AnyAsync(l => l.AlbumId == album.AlbumId && l.SongId == songId, cancellationToken))
            {
                var taken = _db.AlbumSongs.Where(l => l.AlbumId == album.AlbumId).Select(l => l.TrackNumber);

                _db.AlbumSongs.Add(new AlbumSong
                {
                    AlbumId = album.AlbumId,
                    SongId = songId,
                    TrackNumber = await FreeNumberAsync(taken, trackNumber, cancellationToken)
                });
            }

            if (single is not null &&
                !await _db.SingleSongs.AnyAsync(l => l.SingleId == single.SingleId && l.SongId == songId, cancellationToken))
            {
                var taken = _db.SingleSongs.Where(l => l.SingleId == single.SingleId).Select(l => l.TrackNumber);

                _db.SingleSongs.Add(new SingleSong
                {
                    SingleId = single.SingleId,
                    SongId = songId,
                    TrackNumber = await FreeNumberAsync(taken, trackNumber, cancellationToken)
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        private static async Task<int> FreeNumberAsync(IQueryable<int?> taken, int wanted, CancellationToken cancellationToken)
        {
            var numbers = await taken.Where(number => number != null).Select(number => number!.Value).ToListAsync(cancellationToken);

            return numbers.Contains(wanted) ? numbers.Max() + 1 : wanted;
        }

        private async Task<int> LabelAsync(string? label, CancellationToken cancellationToken)
        {
            var name = Fit(string.IsNullOrWhiteSpace(label) ? UnknownLabel : label, ProductionCompany.MaxNameLength);
            var pattern = ContentRows.EscapeLike(name);

            var existing = await _db.ProductionCompanies
                .Where(p => EF.Functions.ILike(p.Name, pattern, "\\"))
                .OrderBy(p => p.ProductionCompanyId)
                .Select(p => (int?)p.ProductionCompanyId)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                return existing.Value;
            }

            var company = new ProductionCompany { Name = name };

            _db.ProductionCompanies.Add(company);

            await _db.SaveChangesAsync(cancellationToken);

            return company.ProductionCompanyId;
        }

        private async Task<List<int>> GenresAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            var ids = new List<int>();

            foreach (var name in names)
            {
                var pattern = ContentRows.EscapeLike(name);

                var existing = await _db.Genres
                    .Where(g => EF.Functions.ILike(g.Name, pattern, "\\"))
                    .OrderBy(g => g.GenreId)
                    .Select(g => (int?)g.GenreId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (existing is null)
                {
                    var genre = new Genre { Name = name };

                    _db.Genres.Add(genre);

                    await _db.SaveChangesAsync(cancellationToken);

                    existing = genre.GenreId;
                }

                if (!ids.Contains(existing.Value))
                {
                    ids.Add(existing.Value);
                }
            }

            return ids;
        }

        private static string Fit(string value, int maxLength)
        {
            var trimmed = value.Trim();

            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private sealed class Counts
        {
            public int Artists;
            public int Albums;
            public int Singles;
            public int Songs;
            public int Existing;
        }
    }
}
