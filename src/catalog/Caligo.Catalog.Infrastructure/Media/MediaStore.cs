using Microsoft.EntityFrameworkCore;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Media;
using Caligo.Catalog.Infrastructure.Persistence;

namespace Caligo.Catalog.Infrastructure.Media
{
    public class MediaStore : IMediaStore
    {
        private readonly StreamingDbContext _db;

        public MediaStore(StreamingDbContext db)
        {
            _db = db;
        }

        public Task<ContentMedia?> FindAsync(int contentId, CancellationToken cancellationToken = default) =>
            _db.ContentMedia.AsNoTracking().FirstOrDefaultAsync(m => m.ContentId == contentId, cancellationToken);

        public async Task<IReadOnlyList<Rendition>> GetRenditionsAsync(
            int contentId, CancellationToken cancellationToken = default) =>
            await _db.Renditions.AsNoTracking()
                .Where(r => r.ContentId == contentId)
                .OrderBy(r => r.RenditionId)
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(
            int contentId, CancellationToken cancellationToken = default) =>
            await (
                from file in _db.SubtitleFiles.AsNoTracking()
                join subtitle in _db.Subtitles on file.SubtitleId equals subtitle.SubtitleId
                join language in _db.Languages on subtitle.LanguageId equals language.LanguageId
                where subtitle.ContentId == contentId
                orderby language.Name, subtitle.SubtitleId
                select new SubtitleTrack(subtitle.SubtitleId, language.Name, file.Uri, file.Format))
                .ToListAsync(cancellationToken);

        public async Task<ContentMedia?> QueueAsync(
            int contentId, string sourceFile, DateTime now, CancellationToken cancellationToken = default)
        {
            var queued = await _db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO streamingservice.contentmedia (content_id, status, source_file, requested_at, attempts)
                VALUES ({contentId}, {MediaStatus.Queued}, {sourceFile}, {SqlValues.Timestamp(now)}, 0)
                ON CONFLICT (content_id) DO UPDATE
                SET status = EXCLUDED.status,
                    source_file = EXCLUDED.source_file,
                    requested_at = EXCLUDED.requested_at,
                    claimed_until = NULL,
                    attempts = 0,
                    error = NULL
                WHERE contentmedia.status <> {MediaStatus.Packaging}
                   OR contentmedia.claimed_until IS NULL
                   OR contentmedia.claimed_until < {SqlValues.Timestamp(now)}
                """,
                cancellationToken);

            return queued == 0 ? null : await FindAsync(contentId, cancellationToken);
        }
    }
}
