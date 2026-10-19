using Microsoft.EntityFrameworkCore;
using Npgsql;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Activity;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    public class ActivityStore : IActivityStore
    {
        private const int MaxAttempts = 3;

        private readonly StreamingDbContext _db;

        public ActivityStore(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<InteractionDto?> GetInteractionAsync(
            int contentId, int userId, CancellationToken cancellationToken = default)
        {
            var row = await _db.Interactions.AsNoTracking()
                .FirstOrDefaultAsync(i => i.ContentId == contentId && i.UserId == userId, cancellationToken);

            return row is null
                ? null
                : new InteractionDto(contentId, row.Liked, row.Rating, await AverageAsync(contentId, cancellationToken));
        }

        public async Task<InteractionDto> SetInteractionAsync(
            int contentId, int userId, bool? liked, float? rating, CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    await _db.Interactions
                        .Where(i => i.ContentId == contentId && i.UserId == userId)
                        .ExecuteDeleteAsync(cancellationToken);

                    _db.Interactions.Add(new UserContentInteraction
                    {
                        ContentId = contentId,
                        UserId = userId,
                        Liked = liked,
                        Rating = rating
                    });

                    await _db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    break;
                }
                catch (DbUpdateException ex) when (
                    attempt < MaxAttempts &&
                    ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    await transaction.RollbackAsync(cancellationToken);

                    _db.ChangeTracker.Clear();
                }
            }

            _db.ChangeTracker.Clear();

            return new InteractionDto(contentId, liked, rating, await AverageAsync(contentId, cancellationToken));
        }

        public async Task<bool> RemoveInteractionAsync(int contentId, int userId, CancellationToken cancellationToken = default) =>
            await _db.Interactions
                .Where(i => i.ContentId == contentId && i.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken) > 0;

        public async Task<CommentDto> AddCommentAsync(
            int contentId, int userId, string text, CancellationToken cancellationToken = default)
        {
            var comment = new Comment { ContentId = contentId, UserId = userId, Text = text };

            _db.Comments.Add(comment);

            await _db.SaveChangesAsync(cancellationToken);

            var author = await _db.Users.AsNoTracking()
                .Where(u => u.UserId == userId)
                .Select(u => new { u.FirstName, u.LastName })
                .FirstAsync(cancellationToken);

            return new CommentDto(
                comment.CommentId, contentId, ContentRows.FullName(author.FirstName, author.LastName), text);
        }

        public async Task<WatchDto> LogWatchAsync(
            int contentId, int userId, float? timestamp, DateTime date, CancellationToken cancellationToken = default)
        {
            var watch = new WatchHistory
            {
                ContentId = contentId,
                UserId = userId,
                Timestamp = timestamp,
                Date = date
            };

            _db.WatchHistory.Add(watch);

            await _db.SaveChangesAsync(cancellationToken);

            var row = await _db.Rows().FirstAsync(r => r.ContentId == contentId, cancellationToken);

            return new WatchDto(watch.WatchedContentId, contentId, row.Title, row.Kind, timestamp, date);
        }

        public async Task<IReadOnlyList<WatchDto>> GetHistoryAsync(
            int userId, int limit, CancellationToken cancellationToken = default)
        {
            var rows = await (
                from w in _db.WatchHistory.AsNoTracking()
                join r in _db.Rows() on w.ContentId equals r.ContentId
                where w.UserId == userId
                orderby w.Date descending, w.WatchedContentId descending
                select new { Watch = w, Row = r }).Take(limit).ToListAsync(cancellationToken);

            return rows
                .Select(x => new WatchDto(
                    x.Watch.WatchedContentId, x.Watch.ContentId, x.Row.Title, x.Row.Kind, x.Watch.Timestamp, x.Watch.Date))
                .ToList();
        }

        public async Task<IReadOnlyList<RecommendationDto>> GetRecommendationsAsync(
            int userId, CancellationToken cancellationToken = default)
        {
            var rows = await (
                from rec in _db.Recommendations.AsNoTracking()
                join r in _db.Rows() on rec.ContentId equals r.ContentId
                where rec.UserId == userId
                orderby rec.RecommendationId
                select new { Recommendation = rec, Row = r }).ToListAsync(cancellationToken);

            return rows
                .Select(x => new RecommendationDto(
                    x.Row.ContentId,
                    x.Row.Title,
                    x.Row.Kind,
                    x.Recommendation.RecommendationReason,
                    x.Recommendation.RecommendationDate))
                .ToList();
        }

        public async Task<IReadOnlyList<NotificationDto>> GetNotificationsAsync(
            int userId, int limit, CancellationToken cancellationToken = default) =>
            await (
                from n in _db.Notifications.AsNoTracking()
                join reason in _db.NotificationReasons on n.ReasonId equals reason.ReasonId
                where n.UserId == userId
                orderby n.NotificationDate descending, n.NotificationId descending
                select new NotificationDto(n.NotificationId, reason.ReasonDescription, n.NotificationDate))
                .Take(limit)
                .ToListAsync(cancellationToken);

        private async Task<float?> AverageAsync(int contentId, CancellationToken cancellationToken) =>
            await _db.Contents.AsNoTracking()
                .Where(c => c.ContentId == contentId)
                .Select(c => c.AverageRating)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
