using Microsoft.EntityFrameworkCore;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Contents;
using Caligo.Catalog.Domain.Media;
using Caligo.Catalog.Infrastructure.Persistence;

namespace Caligo.Catalog.Infrastructure.Media
{
    public class PackagingQueue : IPackagingQueue
    {
        public const string Abandoned = "The packager stopped while working on this title too many times.";

        private readonly StreamingDbContext _db;

        public PackagingQueue(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<PackagingJob?> ClaimAsync(
            DateTime now, DateTime claimedUntil, int maxAttempts, CancellationToken cancellationToken = default)
        {
            await _db.Database.ExecuteSqlAsync(
                $"""
                UPDATE streamingservice.contentmedia
                SET status = {MediaStatus.Failed}, claimed_until = NULL, error = {Abandoned}
                WHERE status = {MediaStatus.Packaging}
                  AND (claimed_until IS NULL OR claimed_until < {SqlValues.Timestamp(now)})
                  AND attempts >= {maxAttempts}
                """,
                cancellationToken);

            var claimed = await _db.Database
                .SqlQuery<PackagingJob>(
                    $"""
                    UPDATE streamingservice.contentmedia
                    SET status = {MediaStatus.Packaging},
                        claimed_until = {SqlValues.Timestamp(claimedUntil)},
                        attempts = attempts + 1,
                        error = NULL
                    WHERE content_id = (
                        SELECT content_id
                        FROM streamingservice.contentmedia
                        WHERE status = {MediaStatus.Queued}
                           OR (status = {MediaStatus.Packaging} AND (claimed_until IS NULL OR claimed_until < {SqlValues.Timestamp(now)}))
                        ORDER BY requested_at, content_id
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED)
                    RETURNING content_id AS "ContentId",
                              source_file AS "SourceFile",
                              attempts AS "Attempts",
                              storage_path AS "StoragePath"
                    """)
                .ToListAsync(cancellationToken);

            return claimed.Count == 0 ? null : claimed[0];
        }

        public async Task<bool> ExtendAsync(
            PackagingJob job, DateTime claimedUntil, CancellationToken cancellationToken = default) =>
            await Held(job)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.ClaimedUntil, (DateTime?)claimedUntil), cancellationToken) > 0;

        public async Task<bool> PublishAsync(
            PackagingJob job, PackagingResult result, DateTime now, CancellationToken cancellationToken = default)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var held = await Held(job)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(m => m.Status, MediaStatus.Ready)
                        .SetProperty(m => m.ClaimedUntil, (DateTime?)null)
                        .SetProperty(m => m.PackagedAt, (DateTime?)now)
                        .SetProperty(m => m.StoragePath, result.StoragePath)
                        .SetProperty(m => m.HlsPath, result.HlsPath)
                        .SetProperty(m => m.DashPath, result.DashPath)
                        .SetProperty(m => m.DurationSeconds, (float?)result.DurationSeconds)
                        .SetProperty(m => m.Error, (string?)null),
                    cancellationToken);

            if (held == 0)
            {
                return false;
            }

            await _db.Renditions.Where(r => r.ContentId == job.ContentId).ExecuteDeleteAsync(cancellationToken);

            _db.Renditions.AddRange(result.Renditions.Select(r => new Rendition
            {
                ContentId = job.ContentId,
                Kind = r.Kind,
                Codec = r.Codec,
                Bitrate = r.Bitrate,
                Width = r.Width,
                Height = r.Height,
                Language = r.Language,
                Path = r.Path
            }));

            var subtitles = await _db.Subtitles
                .Where(s => s.ContentId == job.ContentId)
                .OrderBy(s => s.SubtitleId)
                .ToListAsync(cancellationToken);

            var subtitleIds = subtitles.Select(s => s.SubtitleId).ToList();

            await _db.SubtitleFiles
                .Where(f => subtitleIds.Contains(f.SubtitleId))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var packaged in result.Subtitles.DistinctBy(s => s.LanguageId))
            {
                var subtitle = subtitles.FirstOrDefault(s => s.LanguageId == packaged.LanguageId);

                if (subtitle is null)
                {
                    subtitle = new Subtitle { ContentId = job.ContentId, LanguageId = packaged.LanguageId };

                    _db.Subtitles.Add(subtitle);

                    await _db.SaveChangesAsync(cancellationToken);

                    subtitles.Add(subtitle);
                }

                _db.SubtitleFiles.Add(new SubtitleFile
                {
                    SubtitleId = subtitle.SubtitleId,
                    Uri = packaged.Uri,
                    Format = SubtitleFile.WebVtt
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _db.ChangeTracker.Clear();

            return true;
        }

        public async Task<bool> FailAsync(PackagingJob job, string error, CancellationToken cancellationToken = default)
        {
            var text = error.Length <= ContentMedia.MaxErrorLength ? error : error[..ContentMedia.MaxErrorLength];

            return await Held(job)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(m => m.Status, MediaStatus.Failed)
                        .SetProperty(m => m.ClaimedUntil, (DateTime?)null)
                        .SetProperty(m => m.Error, text),
                    cancellationToken) > 0;
        }

        public async Task<bool> ReleaseAsync(PackagingJob job, CancellationToken cancellationToken = default) =>
            await Held(job)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(m => m.Status, MediaStatus.Queued)
                        .SetProperty(m => m.ClaimedUntil, (DateTime?)null)
                        .SetProperty(m => m.Attempts, m => m.Attempts > 0 ? m.Attempts - 1 : 0),
                    cancellationToken) > 0;

        private IQueryable<ContentMedia> Held(PackagingJob job) =>
            _db.ContentMedia.Where(m =>
                m.ContentId == job.ContentId &&
                m.Status == MediaStatus.Packaging &&
                m.Attempts == job.Attempts &&
                m.SourceFile == job.SourceFile);
    }
}
