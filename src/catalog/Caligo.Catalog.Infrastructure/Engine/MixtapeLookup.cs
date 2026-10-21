using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Engine;
using Caligo.Catalog.Domain.Music;
using Caligo.Catalog.Infrastructure.Persistence;

namespace Caligo.Catalog.Infrastructure.Engine
{
    public class MixtapeLookup : IMixtapeLookup
    {
        private readonly StreamingDbContext _db;
        private readonly ICatalogViewer _viewer;
        private readonly string _prefix;

        public MixtapeLookup(StreamingDbContext db, ICatalogViewer viewer, IOptions<CatalogOptions> options)
        {
            _db = db;
            _viewer = viewer;
            _prefix = options.Value.MixtapeUrlPrefix;
        }

        public async Task<MixtapeTrack?> TrackAsync(int contentId, CancellationToken cancellationToken = default)
        {
            var hidden = await _viewer.HiddenAsync(cancellationToken);

            if (hidden.ContentIds.Contains(contentId))
            {
                return null;
            }

            var song = await (
                from s in _db.Songs.AsNoTracking()
                join c in _db.Contents on s.ContentId equals c.ContentId
                where s.ContentId == contentId
                orderby s.SongId
                select new { s.SongId, s.AudioUri, c.Title }).FirstOrDefaultAsync(cancellationToken);

            if (song is null || MixtapePaths.Split(song.AudioUri, _prefix) is not { } path)
            {
                return null;
            }

            var artist = await (
                from link in _db.SongArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where link.SongId == song.SongId
                orderby link.Role == SongArtist.PrimaryRole descending, a.Name
                select a.Name).FirstOrDefaultAsync(cancellationToken);

            var album = await (
                from link in _db.AlbumSongs.AsNoTracking()
                join a in _db.Albums on link.AlbumId equals a.AlbumId
                where link.SongId == song.SongId
                orderby a.AlbumId
                select a.Title).FirstOrDefaultAsync(cancellationToken)
                ?? await (
                    from link in _db.SingleSongs.AsNoTracking()
                    join s in _db.Singles on link.SingleId equals s.SingleId
                    where link.SongId == song.SongId
                    orderby s.SingleId
                    select s.Title).FirstOrDefaultAsync(cancellationToken);

            return new MixtapeTrack(path.Folder, path.File, song.Title, artist ?? "Unknown Artist", album ?? "Unknown Album");
        }

        public async Task<string?> AlbumFolderAsync(int albumId, CancellationToken cancellationToken = default)
        {
            var hidden = await _viewer.HiddenAsync(cancellationToken);

            if (hidden.AlbumIds.Contains(albumId))
            {
                return null;
            }

            var cover = await _db.Albums.AsNoTracking()
                .Where(a => a.AlbumId == albumId)
                .Select(a => a.CoverUri)
                .FirstOrDefaultAsync(cancellationToken);

            return MixtapePaths.Folder(cover, _prefix) ?? await FolderOfSongsAsync(
                _db.AlbumSongs.Where(link => link.AlbumId == albumId).Select(link => link.SongId), cancellationToken);
        }

        public async Task<string?> SingleFolderAsync(int singleId, CancellationToken cancellationToken = default)
        {
            var hidden = await _viewer.HiddenAsync(cancellationToken);

            if (hidden.SingleIds.Contains(singleId))
            {
                return null;
            }

            var cover = await _db.Singles.AsNoTracking()
                .Where(s => s.SingleId == singleId)
                .Select(s => s.CoverUri)
                .FirstOrDefaultAsync(cancellationToken);

            return MixtapePaths.Folder(cover, _prefix) ?? await FolderOfSongsAsync(
                _db.SingleSongs.Where(link => link.SingleId == singleId).Select(link => link.SongId), cancellationToken);
        }

        private async Task<string?> FolderOfSongsAsync(IQueryable<int> songIds, CancellationToken cancellationToken)
        {
            var uris = await _db.Songs.AsNoTracking()
                .Where(s => songIds.Contains(s.SongId))
                .Select(s => s.AudioUri)
                .ToListAsync(cancellationToken);

            var folders = uris.Select(uri => MixtapePaths.Folder(uri, _prefix)).Distinct().ToList();

            return folders.Count == 1 ? folders[0] : null;
        }
    }
}
