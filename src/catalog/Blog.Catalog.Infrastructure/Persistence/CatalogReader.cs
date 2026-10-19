using Microsoft.EntityFrameworkCore;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Contents;
using Blog.Catalog.Domain.Contents;
using Blog.Catalog.Domain.Music;
using Blog.SharedKernel;

namespace Blog.Catalog.Infrastructure.Persistence
{
    public class CatalogReader : ICatalogReader
    {
        private readonly StreamingDbContext _db;

        public CatalogReader(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<PagedResult<ContentSummaryDto>> GetContentPageAsync(
            string? kind, int? genreId, string? search, ContentCursor? cursor, int limit,
            CancellationToken cancellationToken = default)
        {
            var query = _db.Rows();

            query = kind switch
            {
                ContentKinds.Movie => query.Where(r => r.IsMovie),
                ContentKinds.Episode => query.Where(r => r.IsEpisode),
                ContentKinds.Song => query.Where(r => r.IsSong),
                ContentKinds.Other => query.Where(r => !r.IsMovie && !r.IsEpisode && !r.IsSong),
                _ => query
            };

            if (genreId is { } genre)
            {
                query = query.Where(r => _db.ContentGenres.Any(g => g.ContentId == r.ContentId && g.GenreId == genre));
            }

            if (search is not null)
            {
                var pattern = "%" + ContentRows.EscapeLike(search) + "%";

                query = query.Where(r => EF.Functions.ILike(r.Title, pattern, "\\"));
            }

            if (cursor is { } from)
            {
                query = query.Where(r =>
                    r.ReleaseDate < from.ReleaseDate ||
                    (r.ReleaseDate == from.ReleaseDate && r.ContentId < from.ContentId));
            }

            var rows = await query
                .OrderByDescending(r => r.ReleaseDate)
                .ThenByDescending(r => r.ContentId)
                .Take(limit + 1)
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > limit;

            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var genres = await GenresOfAsync(rows.Select(r => r.ContentId).ToList(), cancellationToken);

            return new PagedResult<ContentSummaryDto>
            {
                Items = rows.Select(r => ToSummary(r, genres)).ToList(),
                NextCursor = hasMore && rows.Count > 0
                    ? new ContentCursor(rows[^1].ReleaseDate, rows[^1].ContentId).Encode()
                    : null
            };
        }

        public async Task<ContentDetailDto?> GetContentAsync(int contentId, CancellationToken cancellationToken = default)
        {
            var row = await _db.Rows().FirstOrDefaultAsync(r => r.ContentId == contentId, cancellationToken);

            if (row is null)
            {
                return null;
            }

            var description = await _db.Contents.AsNoTracking()
                .Where(c => c.ContentId == contentId)
                .Select(c => new { c.Description, c.ProductionCompanyId })
                .FirstAsync(cancellationToken);

            var company = await _db.ProductionCompanies.AsNoTracking()
                .Where(p => p.ProductionCompanyId == description.ProductionCompanyId)
                .Select(p => new NamedDto(p.ProductionCompanyId, p.Name))
                .FirstAsync(cancellationToken);

            var distributors = await (
                from link in _db.ContentDistributingCompanies.AsNoTracking()
                join d in _db.DistributingCompanies on link.DistributingCompanyId equals d.DistributingCompanyId
                where link.ContentId == contentId
                orderby d.Name
                select new NamedDto(d.DistributingCompanyId, d.Name)).ToListAsync(cancellationToken);

            var genres = await (
                from link in _db.ContentGenres.AsNoTracking()
                join g in _db.Genres on link.GenreId equals g.GenreId
                where link.ContentId == contentId
                orderby g.Name
                select new NamedDto(g.GenreId, g.Name)).ToListAsync(cancellationToken);

            var languages = await (
                from link in _db.ContentLanguages.AsNoTracking()
                join l in _db.Languages on link.LanguageId equals l.LanguageId
                where link.ContentId == contentId
                orderby l.Name
                select new NamedDto(l.LanguageId, l.Name)).ToListAsync(cancellationToken);

            var subtitles = await (
                from s in _db.Subtitles.AsNoTracking()
                join l in _db.Languages on s.LanguageId equals l.LanguageId
                where s.ContentId == contentId
                orderby l.Name
                select new NamedDto(l.LanguageId, l.Name)).ToListAsync(cancellationToken);

            var actors = await (
                from link in _db.ContentActors.AsNoTracking()
                join a in _db.Actors on link.ActorId equals a.ActorId
                where link.ContentId == contentId
                orderby a.LastName, a.FirstName
                select new { a.ActorId, a.FirstName, a.LastName }).ToListAsync(cancellationToken);

            var movie = await _db.Movies.AsNoTracking()
                .Where(m => m.ContentId == contentId)
                .OrderBy(m => m.MovieId)
                .Select(m => new MovieDto(m.MovieId, m.Budget, m.BoxOffice))
                .FirstOrDefaultAsync(cancellationToken);

            var episode = await (
                from e in _db.Episodes.AsNoTracking()
                join series in _db.Series on e.SeriesId equals series.SeriesId
                join season in _db.Seasons
                    on new { e.SeriesId, e.SeasonNumber } equals new { season.SeriesId, season.SeasonNumber }
                where e.ContentId == contentId
                select new EpisodeDto(e.SeriesId, series.Title, e.SeasonNumber, season.Name, e.EpisodeNumber))
                .FirstOrDefaultAsync(cancellationToken);

            return new ContentDetailDto
            {
                ContentId = row.ContentId,
                Title = row.Title,
                Description = description.Description,
                Kind = row.Kind,
                Duration = row.Duration,
                ReleaseDate = row.ReleaseDate,
                AverageRating = row.AverageRating,
                ProductionCompany = company,
                DistributingCompanies = distributors,
                Genres = genres,
                Languages = languages,
                Subtitles = subtitles,
                Actors = actors.Select(a => new ContentActorDto(a.ActorId, ContentRows.FullName(a.FirstName, a.LastName))).ToList(),
                Movie = movie,
                Episode = episode,
                Song = await SongOfAsync(contentId, cancellationToken),
                Views = await _db.WatchHistory.CountAsync(w => w.ContentId == contentId, cancellationToken),
                Interactions = await _db.Interactions.CountAsync(i => i.ContentId == contentId, cancellationToken),
                Comments = await _db.Comments.CountAsync(c => c.ContentId == contentId, cancellationToken)
            };
        }

        public Task<bool> ContentExistsAsync(int contentId, CancellationToken cancellationToken = default) =>
            _db.Contents.AnyAsync(c => c.ContentId == contentId, cancellationToken);

        public async Task<IReadOnlyDictionary<int, string>> GetTitlesAsync(
            IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default)
        {
            if (contentIds.Count == 0)
            {
                return new Dictionary<int, string>();
            }

            return await _db.Contents.AsNoTracking()
                .Where(c => contentIds.Contains(c.ContentId))
                .ToDictionaryAsync(c => c.ContentId, c => c.Title, cancellationToken);
        }

        public async Task<IReadOnlyList<SimilarContentDto>> GetSimilarAsync(
            int contentId, int limit, CancellationToken cancellationToken = default)
        {
            var rows = await _db.Rows()
                .Where(r => r.ContentId != contentId)
                .Select(r => new
                {
                    Row = r,
                    Similarity = StreamingDbContext.CalculateSimilarity(contentId, r.ContentId)
                })
                .OrderByDescending(x => x.Similarity)
                .ThenBy(x => x.Row.ContentId)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return rows
                .Select(x => new SimilarContentDto(x.Row.ContentId, x.Row.Title, x.Row.Kind, x.Similarity))
                .ToList();
        }

        public async Task<IReadOnlyList<CommentDto>> GetCommentsAsync(
            int contentId, int? beforeCommentId, int limit, CancellationToken cancellationToken = default)
        {
            var query = from c in _db.Comments.AsNoTracking()
                        join u in _db.Users on c.UserId equals u.UserId
                        where c.ContentId == contentId
                        select new { c.CommentId, c.ContentId, c.Text, u.FirstName, u.LastName };

            if (beforeCommentId is { } before)
            {
                query = query.Where(c => c.CommentId < before);
            }

            var rows = await query.OrderByDescending(c => c.CommentId).Take(limit).ToListAsync(cancellationToken);

            return rows
                .Select(c => new CommentDto(c.CommentId, c.ContentId, ContentRows.FullName(c.FirstName, c.LastName), c.Text))
                .ToList();
        }

        public async Task<IReadOnlyList<SeriesSummaryDto>> GetSeriesListAsync(CancellationToken cancellationToken = default) =>
            await _db.Series.AsNoTracking()
                .OrderBy(s => s.Title)
                .Select(s => new SeriesSummaryDto(
                    s.SeriesId,
                    s.Title,
                    s.ReleaseDate,
                    _db.Seasons.Count(season => season.SeriesId == s.SeriesId)))
                .ToListAsync(cancellationToken);

        public async Task<SeriesDetailDto?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken = default)
        {
            var series = await _db.Series.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SeriesId == seriesId, cancellationToken);

            if (series is null)
            {
                return null;
            }

            var seasons = await _db.Seasons.AsNoTracking()
                .Where(s => s.SeriesId == seriesId)
                .OrderBy(s => s.SeasonNumber)
                .ToListAsync(cancellationToken);

            var episodes = await (
                from e in _db.Episodes.AsNoTracking()
                join c in _db.Contents on e.ContentId equals c.ContentId
                where e.SeriesId == seriesId
                orderby e.SeasonNumber, e.EpisodeNumber
                select new
                {
                    e.SeasonNumber,
                    Episode = new SeriesEpisodeDto(e.EpisodeNumber, c.ContentId, c.Title, c.Duration, c.ReleaseDate)
                }).ToListAsync(cancellationToken);

            return new SeriesDetailDto(
                series.SeriesId,
                series.Title,
                series.Description,
                series.ReleaseDate,
                seasons.Select(s => new SeasonDto(
                    s.SeasonNumber,
                    s.Name,
                    episodes.Where(e => e.SeasonNumber == s.SeasonNumber).Select(e => e.Episode).ToList())).ToList());
        }

        public async Task<ActorDetailDto?> GetActorAsync(int actorId, CancellationToken cancellationToken = default)
        {
            var actor = await _db.Actors.AsNoTracking()
                .FirstOrDefaultAsync(a => a.ActorId == actorId, cancellationToken);

            if (actor is null)
            {
                return null;
            }

            var rows = await _db.Rows()
                .Where(r => _db.ContentActors.Any(link => link.ActorId == actorId && link.ContentId == r.ContentId))
                .OrderByDescending(r => r.ReleaseDate)
                .ThenByDescending(r => r.ContentId)
                .ToListAsync(cancellationToken);

            var genres = await GenresOfAsync(rows.Select(r => r.ContentId).ToList(), cancellationToken);

            return new ActorDetailDto(
                actor.ActorId,
                ContentRows.FullName(actor.FirstName, actor.LastName),
                actor.Dob,
                actor.Nationality,
                actor.Biography,
                actor.Gender.ToString(),
                actor.ProfilePictureUri,
                rows.Select(r => ToSummary(r, genres)).ToList());
        }

        public async Task<IReadOnlyList<ArtistSummaryDto>> GetArtistsAsync(
            string? search, int limit, CancellationToken cancellationToken = default)
        {
            var query = _db.Artists.AsNoTracking();

            if (search is not null)
            {
                var pattern = "%" + ContentRows.EscapeLike(search) + "%";

                query = query.Where(a => EF.Functions.ILike(a.Name, pattern, "\\"));
            }

            return await query
                .OrderBy(a => a.Name)
                .ThenBy(a => a.ArtistId)
                .Take(limit)
                .Select(a => new ArtistSummaryDto(
                    a.ArtistId,
                    a.Name,
                    _db.SongArtists.Count(link => link.ArtistId == a.ArtistId)))
                .ToListAsync(cancellationToken);
        }

        public async Task<ArtistDetailDto?> GetArtistAsync(int artistId, CancellationToken cancellationToken = default)
        {
            var artist = await _db.Artists.AsNoTracking()
                .FirstOrDefaultAsync(a => a.ArtistId == artistId, cancellationToken);

            if (artist is null)
            {
                return null;
            }

            var albumIds = await _db.AlbumArtists.AsNoTracking()
                .Where(link => link.ArtistId == artistId)
                .Select(link => link.AlbumId)
                .ToListAsync(cancellationToken);

            var singles = await (
                from link in _db.SingleArtists.AsNoTracking()
                join s in _db.Singles on link.SingleId equals s.SingleId
                where link.ArtistId == artistId
                orderby s.ReleaseDate descending, s.SingleId descending
                select new NamedDto(s.SingleId, s.Title)).ToListAsync(cancellationToken);

            var songIds = await _db.SongArtists.AsNoTracking()
                .Where(link => link.ArtistId == artistId)
                .Select(link => link.SongId)
                .ToListAsync(cancellationToken);

            var tracks = await TracksAsync(songIds, cancellationToken);

            return new ArtistDetailDto(
                artist.ArtistId,
                artist.Name,
                artist.Country,
                artist.FormedDate,
                artist.Biography,
                artist.PictureUri,
                await AlbumSummariesAsync(albumIds, cancellationToken),
                singles,
                tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList());
        }

        public async Task<IReadOnlyList<AlbumSummaryDto>> GetAlbumsAsync(
            string? search, int limit, CancellationToken cancellationToken = default)
        {
            var query = _db.Albums.AsNoTracking();

            if (search is not null)
            {
                var pattern = "%" + ContentRows.EscapeLike(search) + "%";

                query = query.Where(a => EF.Functions.ILike(a.Title, pattern, "\\"));
            }

            var ids = await query
                .OrderBy(a => a.Title)
                .ThenBy(a => a.AlbumId)
                .Take(limit)
                .Select(a => a.AlbumId)
                .ToListAsync(cancellationToken);

            return await AlbumSummariesAsync(ids, cancellationToken);
        }

        public async Task<AlbumDetailDto?> GetAlbumAsync(int albumId, CancellationToken cancellationToken = default)
        {
            var album = await _db.Albums.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AlbumId == albumId, cancellationToken);

            if (album is null)
            {
                return null;
            }

            var artists = await (
                from link in _db.AlbumArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where link.AlbumId == albumId
                orderby a.Name
                select new NamedDto(a.ArtistId, a.Name)).ToListAsync(cancellationToken);

            var listing = await _db.AlbumSongs.AsNoTracking()
                .Where(link => link.AlbumId == albumId)
                .ToListAsync(cancellationToken);

            var tracks = await TracksAsync(listing.Select(l => l.SongId).ToList(), cancellationToken);
            var positions = listing.ToDictionary(l => l.SongId);

            return new AlbumDetailDto(
                album.AlbumId,
                album.Title,
                album.Description,
                album.ReleaseDate,
                album.CoverUri,
                artists,
                tracks
                    .Select(t => t with
                    {
                        DiscNumber = positions[t.SongId].DiscNumber,
                        TrackNumber = positions[t.SongId].TrackNumber
                    })
                    .OrderBy(t => t.DiscNumber)
                    .ThenBy(t => t.TrackNumber ?? int.MaxValue)
                    .ThenBy(t => t.SongId)
                    .ToList());
        }

        public async Task<SongMatchDto?> FindSongAsync(
            string artist, string title, CancellationToken cancellationToken = default)
        {
            var name = artist.Length <= Artist.MaxNameLength ? artist : artist[..Artist.MaxNameLength];
            var artistPattern = ContentRows.EscapeLike(name);
            var titlePattern = ContentRows.EscapeLike(Content.FitTitle(title));

            return await (
                from s in _db.Songs.AsNoTracking()
                join c in _db.Contents on s.ContentId equals c.ContentId
                join link in _db.SongArtists on s.SongId equals link.SongId
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where EF.Functions.ILike(c.Title, titlePattern, "\\") && EF.Functions.ILike(a.Name, artistPattern, "\\")
                orderby s.SongId
                select new SongMatchDto(c.ContentId, s.SongId, c.Title, a.Name)).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<SingleDetailDto?> GetSingleAsync(int singleId, CancellationToken cancellationToken = default)
        {
            var single = await _db.Singles.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SingleId == singleId, cancellationToken);

            if (single is null)
            {
                return null;
            }

            var artists = await (
                from link in _db.SingleArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where link.SingleId == singleId
                orderby a.Name
                select new NamedDto(a.ArtistId, a.Name)).ToListAsync(cancellationToken);

            var listing = await _db.SingleSongs.AsNoTracking()
                .Where(link => link.SingleId == singleId)
                .ToListAsync(cancellationToken);

            var tracks = await TracksAsync(listing.Select(l => l.SongId).ToList(), cancellationToken);
            var positions = listing.ToDictionary(l => l.SongId, l => l.TrackNumber);

            return new SingleDetailDto(
                single.SingleId,
                single.Title,
                single.ReleaseDate,
                single.CoverUri,
                artists,
                tracks
                    .Select(t => t with { TrackNumber = positions[t.SongId] })
                    .OrderBy(t => t.TrackNumber ?? int.MaxValue)
                    .ThenBy(t => t.SongId)
                    .ToList());
        }

        public async Task<IReadOnlyList<NamedDto>> GetGenresAsync(CancellationToken cancellationToken = default) =>
            await _db.Genres.AsNoTracking()
                .OrderBy(g => g.Name)
                .ThenBy(g => g.GenreId)
                .Select(g => new NamedDto(g.GenreId, g.Name))
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<NamedDto>> GetLanguagesAsync(CancellationToken cancellationToken = default) =>
            await _db.Languages.AsNoTracking()
                .OrderBy(l => l.Name)
                .Select(l => new NamedDto(l.LanguageId, l.Name))
                .ToListAsync(cancellationToken);

        private static ContentSummaryDto ToSummary(ContentRow row, ILookup<int, string> genres) => new()
        {
            ContentId = row.ContentId,
            Title = row.Title,
            Kind = row.Kind,
            Duration = row.Duration,
            ReleaseDate = row.ReleaseDate,
            AverageRating = row.AverageRating,
            Genres = genres[row.ContentId].ToList()
        };

        private async Task<ILookup<int, string>> GenresOfAsync(List<int> contentIds, CancellationToken cancellationToken)
        {
            if (contentIds.Count == 0)
            {
                return Array.Empty<(int, string)>().ToLookup(x => x.Item1, x => x.Item2);
            }

            var rows = await (
                from link in _db.ContentGenres.AsNoTracking()
                join g in _db.Genres on link.GenreId equals g.GenreId
                where contentIds.Contains(link.ContentId)
                orderby g.Name
                select new { link.ContentId, g.Name }).ToListAsync(cancellationToken);

            return rows.ToLookup(r => r.ContentId, r => r.Name);
        }

        private async Task<SongDto?> SongOfAsync(int contentId, CancellationToken cancellationToken)
        {
            var song = await _db.Songs.AsNoTracking()
                .Where(s => s.ContentId == contentId)
                .OrderBy(s => s.SongId)
                .FirstOrDefaultAsync(cancellationToken);

            if (song is null)
            {
                return null;
            }

            var artists = await (
                from link in _db.SongArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where link.SongId == song.SongId
                orderby a.Name
                select new SongArtistDto(a.ArtistId, a.Name, link.Role)).ToListAsync(cancellationToken);

            var albums = await (
                from link in _db.AlbumSongs.AsNoTracking()
                join a in _db.Albums on link.AlbumId equals a.AlbumId
                where link.SongId == song.SongId
                orderby a.Title
                select new SongReleaseDto(a.AlbumId, a.Title, link.TrackNumber)).ToListAsync(cancellationToken);

            var singles = await (
                from link in _db.SingleSongs.AsNoTracking()
                join s in _db.Singles on link.SingleId equals s.SingleId
                where link.SongId == song.SongId
                orderby s.Title
                select new SongReleaseDto(s.SingleId, s.Title, link.TrackNumber)).ToListAsync(cancellationToken);

            return new SongDto(
                song.SongId,
                song.AudioUri,
                song.Lyrics,
                artists.OrderByDescending(a => a.Role == SongArtist.PrimaryRole).ToList(),
                albums,
                singles);
        }

        private async Task<List<TrackDto>> TracksAsync(List<int> songIds, CancellationToken cancellationToken)
        {
            if (songIds.Count == 0)
            {
                return new List<TrackDto>();
            }

            var songs = await (
                from s in _db.Songs.AsNoTracking()
                join c in _db.Contents on s.ContentId equals c.ContentId
                where songIds.Contains(s.SongId)
                select new { s.SongId, s.AudioUri, c.ContentId, c.Title, c.Duration }).ToListAsync(cancellationToken);

            var artists = (await (
                from link in _db.SongArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where songIds.Contains(link.SongId)
                orderby a.Name
                select new { link.SongId, a.Name }).ToListAsync(cancellationToken))
                .ToLookup(x => x.SongId, x => x.Name);

            return songs
                .Select(s => new TrackDto(
                    s.ContentId, s.SongId, s.Title, s.Duration, null, null, s.AudioUri, artists[s.SongId].ToList()))
                .ToList();
        }

        private async Task<List<AlbumSummaryDto>> AlbumSummariesAsync(List<int> albumIds, CancellationToken cancellationToken)
        {
            if (albumIds.Count == 0)
            {
                return new List<AlbumSummaryDto>();
            }

            var albums = await _db.Albums.AsNoTracking()
                .Where(a => albumIds.Contains(a.AlbumId))
                .Select(a => new
                {
                    Album = a,
                    Songs = _db.AlbumSongs.Count(link => link.AlbumId == a.AlbumId)
                })
                .ToListAsync(cancellationToken);

            var artists = (await (
                from link in _db.AlbumArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where albumIds.Contains(link.AlbumId)
                orderby a.Name
                select new { link.AlbumId, a.Name }).ToListAsync(cancellationToken))
                .ToLookup(x => x.AlbumId, x => x.Name);

            return albums
                .OrderBy(a => a.Album.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Album.AlbumId)
                .Select(a => new AlbumSummaryDto(
                    a.Album.AlbumId,
                    a.Album.Title,
                    a.Album.ReleaseDate,
                    a.Album.CoverUri,
                    artists[a.Album.AlbumId].ToList(),
                    a.Songs))
                .ToList();
        }
    }
}
