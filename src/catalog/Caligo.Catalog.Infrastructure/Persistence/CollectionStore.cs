using Microsoft.EntityFrameworkCore;
using Npgsql;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Collections;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Collections;
using Caligo.Catalog.Domain.Music;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    public class CollectionStore : ICollectionStore
    {
        private readonly StreamingDbContext _db;

        public CollectionStore(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<CollectionSummaryDto>> ListAsync(
            string identityId, HiddenMusic hidden, CancellationToken cancellationToken = default)
        {
            var hiddenContent = hidden.ContentIds;

            var collections = await _db.Collections.AsNoTracking()
                .Where(c => c.IdentityId == identityId)
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    c.CollectionId,
                    c.Name,
                    Titles = _db.CollectionTitles.Count(t => t.CollectionId == c.CollectionId && !hiddenContent.Contains(t.ContentId)),
                    Songs = _db.CollectionTitles.Count(t => t.CollectionId == c.CollectionId && !hiddenContent.Contains(t.ContentId)
                        && _db.Songs.Any(s => s.ContentId == t.ContentId))
                })
                .ToListAsync(cancellationToken);

            var ids = collections.Select(c => c.CollectionId).ToList();

            var covers = await (
                from t in _db.CollectionTitles.AsNoTracking()
                join s in _db.Songs on t.ContentId equals s.ContentId
                join link in _db.AlbumSongs on s.SongId equals link.SongId
                join a in _db.Albums on link.AlbumId equals a.AlbumId
                where ids.Contains(t.CollectionId) && a.CoverUri != null && !hiddenContent.Contains(t.ContentId)
                orderby t.CollectionId, t.Position
                select new { t.CollectionId, a.CoverUri }).ToListAsync(cancellationToken);

            var cover = covers.GroupBy(c => c.CollectionId).ToDictionary(g => g.Key, g => g.First().CoverUri);

            return collections
                .Select(c => new CollectionSummaryDto(c.CollectionId, c.Name, c.Titles, c.Songs, cover.GetValueOrDefault(c.CollectionId)))
                .ToList();
        }

        public async Task<CollectionDetailDto?> GetAsync(
            int collectionId, string identityId, HiddenMusic hidden, CancellationToken cancellationToken = default)
        {
            var collection = await _db.Collections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CollectionId == collectionId && c.IdentityId == identityId, cancellationToken);

            if (collection is null)
            {
                return null;
            }

            var hiddenContent = hidden.ContentIds;

            var titles = await (
                from t in _db.CollectionTitles.AsNoTracking()
                join row in _db.Rows() on t.ContentId equals row.ContentId
                where t.CollectionId == collectionId && !hiddenContent.Contains(t.ContentId)
                orderby t.Position
                select new { t.ContentId, t.Position, row.Title, row.Duration, row.IsMovie, row.IsEpisode, row.IsSong })
                .ToListAsync(cancellationToken);

            var contentIds = titles.Select(t => t.ContentId).ToList();

            var songs = await _db.Songs.AsNoTracking()
                .Where(s => contentIds.Contains(s.ContentId))
                .Select(s => new { s.SongId, s.ContentId, s.AudioUri })
                .ToListAsync(cancellationToken);

            var songIds = songs.Select(s => s.SongId).ToList();

            var artists = (await (
                from link in _db.SongArtists.AsNoTracking()
                join a in _db.Artists on link.ArtistId equals a.ArtistId
                where songIds.Contains(link.SongId)
                orderby link.Role == SongArtist.PrimaryRole descending, a.Name
                select new { link.SongId, a.Name }).ToListAsync(cancellationToken))
                .ToLookup(x => x.SongId, x => x.Name);

            var albums = (await (
                from link in _db.AlbumSongs.AsNoTracking()
                join a in _db.Albums on link.AlbumId equals a.AlbumId
                where songIds.Contains(link.SongId)
                orderby a.AlbumId
                select new { link.SongId, a.AlbumId, a.Title, a.CoverUri }).ToListAsync(cancellationToken))
                .GroupBy(x => x.SongId)
                .ToDictionary(g => g.Key, g => g.First());

            var songByContent = songs.GroupBy(s => s.ContentId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.SongId).First());

            var items = titles.Select(t =>
            {
                var song = songByContent.GetValueOrDefault(t.ContentId);
                var album = song is null ? null : albums.GetValueOrDefault(song.SongId);
                var kind = t.IsMovie ? ContentKinds.Movie : t.IsEpisode ? ContentKinds.Episode : t.IsSong ? ContentKinds.Song : ContentKinds.Other;

                return new CollectionTitleDto(
                    t.ContentId,
                    t.Title,
                    kind,
                    t.Duration,
                    song?.AudioUri,
                    song is null ? new List<string>() : artists[song.SongId].ToList(),
                    album?.AlbumId,
                    album?.Title,
                    album?.CoverUri,
                    t.Position);
            }).ToList();

            return new CollectionDetailDto(collection.CollectionId, collection.Name, items);
        }

        public Task<bool> OwnsAsync(int collectionId, string identityId, CancellationToken cancellationToken = default) =>
            _db.Collections.AnyAsync(c => c.CollectionId == collectionId && c.IdentityId == identityId, cancellationToken);

        public Task<bool> NameTakenAsync(
            string identityId, string name, int? exceptCollectionId, CancellationToken cancellationToken = default) =>
            _db.Collections.AnyAsync(
                c => c.IdentityId == identityId && c.Name == name && (exceptCollectionId == null || c.CollectionId != exceptCollectionId),
                cancellationToken);

        public async Task<CollectionSummaryDto?> CreateAsync(
            string identityId, string name, DateTime createdAt, CancellationToken cancellationToken = default)
        {
            var collection = new Collection { IdentityId = identityId, Name = name, CreatedAt = createdAt };

            _db.Collections.Add(collection);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                _db.Entry(collection).State = EntityState.Detached;

                return null;
            }

            return new CollectionSummaryDto(collection.CollectionId, collection.Name, 0, 0, null);
        }

        public async Task<bool> RenameAsync(int collectionId, string name, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _db.Collections
                    .Where(c => c.CollectionId == collectionId)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), cancellationToken) > 0;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return false;
            }
        }

        public Task DeleteAsync(int collectionId, CancellationToken cancellationToken = default) =>
            _db.Collections.Where(c => c.CollectionId == collectionId).ExecuteDeleteAsync(cancellationToken);

        public async Task<IReadOnlyList<int>> MissingContentAsync(
            IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default)
        {
            var ids = contentIds.ToList();

            var existing = await _db.Contents.AsNoTracking()
                .Where(c => ids.Contains(c.ContentId))
                .Select(c => c.ContentId)
                .ToListAsync(cancellationToken);

            return ids.Except(existing).ToList();
        }

        public async Task<AddTitlesResult> AddAsync(
            int collectionId, IReadOnlyList<int> contentIds, DateTime addedAt, CancellationToken cancellationToken = default)
        {
            var ids = contentIds.ToList();

            var present = await _db.CollectionTitles.AsNoTracking()
                .Where(t => t.CollectionId == collectionId && ids.Contains(t.ContentId))
                .Select(t => t.ContentId)
                .ToListAsync(cancellationToken);

            var next = (await _db.CollectionTitles
                .Where(t => t.CollectionId == collectionId)
                .MaxAsync(t => (int?)t.Position, cancellationToken) ?? 0) + 1;

            var added = 0;

            foreach (var contentId in ids.Where(id => !present.Contains(id)))
            {
                _db.CollectionTitles.Add(new CollectionTitle
                {
                    CollectionId = collectionId,
                    ContentId = contentId,
                    Position = next++,
                    AddedAt = addedAt
                });
                added++;
            }

            if (added > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new AddTitlesResult(added, present.Count);
        }

        public async Task<bool> RemoveAsync(int collectionId, int contentId, CancellationToken cancellationToken = default) =>
            await _db.CollectionTitles
                .Where(t => t.CollectionId == collectionId && t.ContentId == contentId)
                .ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
