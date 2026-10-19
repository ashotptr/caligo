using Microsoft.EntityFrameworkCore;
using Npgsql;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Activity;
using Caligo.Catalog.Domain.Media;
using Caligo.Catalog.Infrastructure.Persistence;

namespace Caligo.Catalog.Infrastructure.Media
{
    public class PlaybackStore : IPlaybackStore
    {
        private readonly StreamingDbContext _db;

        public PlaybackStore(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<OpenedSession> OpenAsync(OpenSession request, CancellationToken cancellationToken = default)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            await _db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM streamingservice."user" WHERE user_id = {request.UserId} FOR NO KEY UPDATE""",
                cancellationToken);

            await _db.PlaybackSessions
                .Where(s => s.UserId == request.UserId && (s.EndedAt ?? s.ExpiresAt) < request.PurgeBefore)
                .ExecuteDeleteAsync(cancellationToken);

            var live = await _db.PlaybackSessions
                .Where(s => s.UserId == request.UserId && s.EndedAt == null && s.ExpiresAt > request.Now)
                .ToListAsync(cancellationToken);

            var sameTitle = live
                .Where(s => s.ContentId == request.ContentId && s.DeviceId == request.DeviceId)
                .ToList();

            var reusable = sameTitle.FirstOrDefault(s => !s.IsCompleted && s.StartedAt > request.ReuseStartedAfter);

            if (reusable is not null)
            {
                reusable.ExpiresAt = request.ExpiresAt;

                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new OpenedSession(reusable, true, live.Count);
            }

            foreach (var stale in sameTitle)
            {
                stale.EndedAt = request.Now;
            }

            var active = live.Count - sameTitle.Count;

            if (active >= request.MaxStreams)
            {
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new OpenedSession(null, false, active);
            }

            var session = new PlaybackSession
            {
                SessionId = Guid.NewGuid(),
                UserId = request.UserId,
                ContentId = request.ContentId,
                DeviceId = request.DeviceId,
                StartedAt = request.Now,
                ExpiresAt = request.ExpiresAt
            };

            _db.PlaybackSessions.Add(session);

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new OpenedSession(session, false, active + 1);
        }

        public Task<PlaybackSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _db.PlaybackSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);

        public Task<MediaAccess?> GetAccessAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            (from session in _db.PlaybackSessions.AsNoTracking()
             join media in _db.ContentMedia.AsNoTracking() on session.ContentId equals media.ContentId into packaged
             from media in packaged.DefaultIfEmpty()
             where session.SessionId == sessionId
             select new MediaAccess(session.ExpiresAt, session.EndedAt, media.StoragePath))
            .FirstOrDefaultAsync(cancellationToken);

        public Task<PlaybackPosition?> GetPositionAsync(
            int userId, int contentId, CancellationToken cancellationToken = default) =>
            _db.PlaybackPositions.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ContentId == contentId, cancellationToken);

        public async Task<SavedPosition?> SavePositionAsync(
            PlaybackSession session, float position, long seq, DateTime now, DateTime expiresAt,
            CancellationToken cancellationToken = default)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var renewed = await _db.PlaybackSessions
                .Where(s => s.SessionId == session.SessionId && s.EndedAt == null && s.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAt, expiresAt), cancellationToken);

            if (renewed == 0)
            {
                return null;
            }

            await _db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO streamingservice.playbackposition (user_id, content_id, "position", session_id, seq, updated_at)
                VALUES ({session.UserId}, {session.ContentId}, {position}, {session.SessionId}, {seq}, {SqlValues.Timestamp(now)})
                ON CONFLICT (user_id, content_id) DO UPDATE
                SET "position" = EXCLUDED."position",
                    session_id = EXCLUDED.session_id,
                    seq = EXCLUDED.seq,
                    updated_at = EXCLUDED.updated_at
                WHERE playbackposition.session_id IS DISTINCT FROM EXCLUDED.session_id
                   OR playbackposition.seq < EXCLUDED.seq
                """,
                cancellationToken);

            var stored = await _db.PlaybackPositions.AsNoTracking()
                .FirstAsync(p => p.UserId == session.UserId && p.ContentId == session.ContentId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new SavedPosition(stored.Position, stored.Seq, expiresAt);
        }

        public async Task<Completion> CompleteAsync(
            Guid sessionId, int userId, Guid completionId, float? position, DateTime now,
            CancellationToken cancellationToken = default)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var locked = await _db.PlaybackSessions
                .FromSql($"""
                    SELECT * FROM streamingservice.playbacksession
                    WHERE session_id = {sessionId} AND user_id = {userId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

            if (locked.Count == 0)
            {
                return new Completion(CompletionOutcome.Missing, null);
            }

            var session = locked[0];

            if (session.CompletionId is { } first)
            {
                return new Completion(
                    first == completionId ? CompletionOutcome.Repeated : CompletionOutcome.Mismatch, session);
            }

            var timestamp = position ?? await _db.PlaybackPositions.AsNoTracking()
                .Where(p => p.UserId == userId && p.ContentId == session.ContentId)
                .Select(p => (float?)p.Position)
                .FirstOrDefaultAsync(cancellationToken);

            var watch = new WatchHistory
            {
                ContentId = session.ContentId,
                UserId = userId,
                Timestamp = timestamp,
                Date = now
            };

            try
            {
                _db.WatchHistory.Add(watch);

                await _db.SaveChangesAsync(cancellationToken);

                session.CompletionId = completionId;
                session.CompletedAt = now;
                session.WatchedContentId = watch.WatchedContentId;

                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await transaction.RollbackAsync(cancellationToken);

                _db.ChangeTracker.Clear();

                return new Completion(CompletionOutcome.Mismatch, null);
            }

            await _db.PlaybackPositions
                .Where(p => p.UserId == userId && p.ContentId == session.ContentId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new Completion(CompletionOutcome.Completed, session);
        }

        public async Task<bool> EndAsync(
            Guid sessionId, int userId, DateTime now, CancellationToken cancellationToken = default)
        {
            var ended = await _db.PlaybackSessions
                .Where(s => s.SessionId == sessionId && s.UserId == userId && s.EndedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.EndedAt, (DateTime?)now), cancellationToken);

            return ended > 0 || await _db.PlaybackSessions
                .AnyAsync(s => s.SessionId == sessionId && s.UserId == userId, cancellationToken);
        }
    }
}
