using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Infrastructure.Persistence;

namespace Caligo.Catalog.Infrastructure.Engine
{
    public class MixtapeVisibility
    {
        private readonly StreamingDbContext _db;
        private readonly ICaligoEngine _engine;
        private readonly MixtapeVisibilityCache _cache;
        private readonly string _prefix;

        public MixtapeVisibility(
            StreamingDbContext db, ICaligoEngine engine, MixtapeVisibilityCache cache, IOptions<CatalogOptions> options)
        {
            _db = db;
            _engine = engine;
            _cache = cache;
            _prefix = options.Value.MixtapeUrlPrefix;
        }

        public async Task<HiddenMusic> HiddenForAsync(string? user, CancellationToken cancellationToken = default)
        {
            if (!_engine.IsConfigured)
            {
                return HiddenMusic.None;
            }

            var prefix = _prefix;

            var songs = await _db.Songs.AsNoTracking()
                .Where(s => s.AudioUri != null && s.AudioUri.StartsWith(prefix))
                .Select(s => new { s.SongId, s.ContentId, s.AudioUri })
                .ToListAsync(cancellationToken);

            var albums = await _db.Albums.AsNoTracking()
                .Where(a => a.CoverUri != null && a.CoverUri.StartsWith(prefix))
                .Select(a => new { a.AlbumId, a.CoverUri })
                .ToListAsync(cancellationToken);

            var singles = await _db.Singles.AsNoTracking()
                .Where(s => s.CoverUri != null && s.CoverUri.StartsWith(prefix))
                .Select(s => new { s.SingleId, s.CoverUri })
                .ToListAsync(cancellationToken);

            if (songs.Count == 0 && albums.Count == 0 && singles.Count == 0)
            {
                return HiddenMusic.None;
            }

            var visible = await _cache.VisibleAsync(user, _engine, cancellationToken);

            bool Hidden(string? uri) => MixtapePaths.Folder(uri, prefix) is { } folder && !visible.Contains(folder);

            var hiddenSongs = songs.Where(s => Hidden(s.AudioUri)).ToList();
            var hiddenSongIds = hiddenSongs.Select(s => s.SongId).ToHashSet();

            var hiddenAlbums = albums.Where(a => Hidden(a.CoverUri)).Select(a => a.AlbumId).ToHashSet();
            var hiddenSingles = singles.Where(s => Hidden(s.CoverUri)).Select(s => s.SingleId).ToHashSet();

            if (hiddenSongIds.Count > 0)
            {
                var hiddenList = hiddenSongIds.ToList();

                var albumLinks = await _db.AlbumSongs.AsNoTracking()
                    .Where(link => _db.AlbumSongs.Any(other => other.AlbumId == link.AlbumId && hiddenList.Contains(other.SongId)))
                    .Select(link => new { link.AlbumId, link.SongId })
                    .ToListAsync(cancellationToken);

                foreach (var album in albumLinks.GroupBy(link => link.AlbumId))
                {
                    if (album.All(link => hiddenSongIds.Contains(link.SongId)))
                    {
                        hiddenAlbums.Add(album.Key);
                    }
                }

                var singleLinks = await _db.SingleSongs.AsNoTracking()
                    .Where(link => _db.SingleSongs.Any(other => other.SingleId == link.SingleId && hiddenList.Contains(other.SongId)))
                    .Select(link => new { link.SingleId, link.SongId })
                    .ToListAsync(cancellationToken);

                foreach (var single in singleLinks.GroupBy(link => link.SingleId))
                {
                    if (single.All(link => hiddenSongIds.Contains(link.SongId)))
                    {
                        hiddenSingles.Add(single.Key);
                    }
                }
            }

            return new HiddenMusic(
                hiddenSongs.Select(s => s.ContentId).Distinct().ToArray(),
                hiddenSongIds.ToArray(),
                hiddenAlbums.ToArray(),
                hiddenSingles.ToArray());
        }
    }
}
