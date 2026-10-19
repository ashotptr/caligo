using System.Data;
using Microsoft.EntityFrameworkCore;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Application.Common;
using Caligo.Posts.Application.Posts;
using Caligo.Posts.Domain.Posts;
using Caligo.SharedKernel;

namespace Caligo.Posts.Infrastructure.Persistence
{
    public class EfPostRepository : IPostRepository
    {
        private const int MaxSerializableAttempts = 10;

        private readonly ApplicationDbContext _context;
        private readonly IDomainEventDispatcher _events;

        public EfPostRepository(ApplicationDbContext context, IDomainEventDispatcher events)
        {
            _context = context;
            _events = events;
        }

        public async Task<PagedResult<PostSummaryDto>> GetPageAsync(
            PostCursor? cursor, int limit, string? authorId, CancellationToken cancellationToken = default)
        {
            var query = _context.BlogPosts.AsNoTracking();

            query = authorId is null
                ? query.Where(p => p.Status == PostStatus.Published)
                : query.Where(p => p.AuthorId == authorId);

            return await PageAsync(query, cursor, limit, cancellationToken);
        }

        public Task<PagedResult<PostSummaryDto>> GetPageForContentAsync(
            int contentId, PostCursor? cursor, int limit, CancellationToken cancellationToken = default) =>
            PageAsync(
                _context.BlogPosts.AsNoTracking()
                    .Where(p => p.Status == PostStatus.Published && p.ContentId == contentId),
                cursor,
                limit,
                cancellationToken);

        private async Task<PagedResult<PostSummaryDto>> PageAsync(
            IQueryable<BlogPost> query, PostCursor? cursor, int limit, CancellationToken cancellationToken)
        {
            if (cursor is { } from)
            {
                query = _context.Database.IsNpgsql()
                    ? query.Where(p => EF.Functions.LessThan(
                        ValueTuple.Create(p.PublishedDate, p.Id),
                        ValueTuple.Create(from.PublishedDate, from.Id)))
                    : query.Where(p =>
                        p.PublishedDate < from.PublishedDate ||
                        (p.PublishedDate == from.PublishedDate && p.Id < from.Id));
            }

            var rows = await Project(query
                    .OrderByDescending(p => p.PublishedDate)
                    .ThenByDescending(p => p.Id)
                    .Take(limit + 1))
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > limit;

            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return new PagedResult<PostSummaryDto>
            {
                Items = rows.Select(ToSummary).ToList(),
                NextCursor = hasMore && rows.Count > 0
                    ? new PostCursor(rows[^1].PublishedDate, rows[^1].Id).Encode()
                    : null
            };
        }

        public async Task<PostDetailDto?> GetDetailAsync(int id, CancellationToken cancellationToken = default)
        {
            var row = await _context.BlogPosts
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    p.Content,
                    p.PublishedDate,
                    AuthorName = _context.Users
                        .Where(u => u.Id == p.AuthorId)
                        .Select(u => u.UserName ?? "Unknown")
                        .FirstOrDefault() ?? "Unknown",
                    p.AuthorId,
                    Tags = p.Tags.ToList(),
                    p.Status,
                    p.ScheduledFor,
                    p.Version,
                    p.ContentId
                })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null
                ? null
                : new PostDetailDto
                {
                    Id = row.Id,
                    Title = row.Title,
                    Content = row.Content,
                    PublishedDate = row.PublishedDate,
                    AuthorName = row.AuthorName,
                    AuthorId = row.AuthorId,
                    Tags = row.Tags,
                    Status = StatusName(row.Status),
                    ScheduledFor = row.ScheduledFor,
                    Version = row.Version,
                    ContentId = row.ContentId
                };
        }

        public async Task<PostHeader?> GetHeaderAsync(int id, CancellationToken cancellationToken = default)
        {
            var row = await _context.BlogPosts
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { p.Id, p.AuthorId, p.Status })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null ? null : new PostHeader(row.Id, row.AuthorId, row.Status == PostStatus.Published);
        }

        public Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default) =>
            _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        public void Add(BlogPost post) => _context.BlogPosts.Add(post);

        public void Remove(BlogPost post) => _context.BlogPosts.Remove(post);

        public async Task<bool> TryAddScheduledAsync(
            BlogPost post, int maxScheduledPerAuthor, CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);

                    var scheduled = await _context.BlogPosts.CountAsync(
                        p => p.AuthorId == post.AuthorId && p.Status == PostStatus.Scheduled,
                        cancellationToken);

                    if (scheduled >= maxScheduledPerAuthor)
                    {
                        return false;
                    }

                    _context.BlogPosts.Add(post);

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return true;
                }
                catch (Exception ex) when (PostgresConflicts.IsRetryable(ex))
                {
                    _context.ChangeTracker.Clear();

                    if (attempt == MaxSerializableAttempts)
                    {
                        throw new ConcurrencyConflictException(
                            "The scheduling quota check kept colliding with other requests.", ex);
                    }

                    await Task.Delay(PostgresConflicts.Backoff(attempt), cancellationToken);
                }
            }
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var sources = _context.CaptureEventSources();

                if (sources.Count == 0)
                {
                    return await _context.SaveChangesAsync(cancellationToken);
                }

                if (_context.Database.CurrentTransaction is not null)
                {
                    return await SaveWithEventsAsync(sources, cancellationToken);
                }

                var strategy = _context.Database.CreateExecutionStrategy();

                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                    var written = await SaveWithEventsAsync(sources, cancellationToken);

                    await transaction.CommitAsync(cancellationToken);

                    return written;
                });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConcurrencyConflictException("The post was changed or deleted concurrently.", ex);
            }
        }

        private async Task<int> SaveWithEventsAsync(IReadOnlyList<BlogPost> sources, CancellationToken cancellationToken)
        {
            var written = await _context.SaveChangesAsync(cancellationToken);

            var events = ApplicationDbContext.DrainEvents(sources);

            await _events.DispatchAsync(events, cancellationToken);

            return written;
        }

        private IQueryable<PostRow> Project(IQueryable<BlogPost> query) =>
            query.Select(p => new PostRow
            {
                Id = p.Id,
                Title = p.Title,
                Content = p.Content,
                PublishedDate = p.PublishedDate,
                AuthorName = _context.Users
                    .Where(u => u.Id == p.AuthorId)
                    .Select(u => u.UserName ?? "Unknown")
                    .FirstOrDefault() ?? "Unknown",
                Tags = p.Tags.ToList(),
                Status = p.Status,
                ScheduledFor = p.ScheduledFor,
                ContentId = p.ContentId
            });

        internal static PostSummaryDto ToSummary(PostRow row) => new()
        {
            Id = row.Id,
            Title = row.Title,
            Excerpt = Excerpt.From(row.Content),
            PublishedDate = row.PublishedDate,
            AuthorName = row.AuthorName,
            Tags = row.Tags,
            Status = StatusName(row.Status),
            ScheduledFor = row.ScheduledFor,
            ContentId = row.ContentId
        };

        internal static string StatusName(PostStatus status) => status == PostStatus.Scheduled
            ? PostStatuses.Scheduled
            : PostStatuses.Published;

        internal sealed class PostRow
        {
            public int Id { get; init; }
            public string Title { get; init; } = string.Empty;
            public string Content { get; init; } = string.Empty;
            public DateTime PublishedDate { get; init; }
            public string AuthorName { get; init; } = string.Empty;
            public List<string> Tags { get; init; } = new();
            public PostStatus Status { get; init; }
            public DateTime? ScheduledFor { get; init; }
            public int? ContentId { get; init; }
        }
    }
}
