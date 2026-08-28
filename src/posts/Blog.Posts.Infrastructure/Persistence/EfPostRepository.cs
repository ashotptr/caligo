using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Posts.Infrastructure.Persistence
{
    public class EfPostRepository : IPostRepository
    {
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

            if (authorId is not null)
            {
                query = query.Where(p => p.AuthorId == authorId);
            }

            if (cursor is { } from)
            {
                // Strict "less than" on the composite (date, id) key. Comparing the pair rather
                // than the date alone is what keeps posts published in the same tick from being
                // skipped or repeated across a page boundary.
                query = query.Where(p =>
                    p.PublishedDate < from.PublishedDate ||
                    (p.PublishedDate == from.PublishedDate && p.Id < from.Id));
            }

            // One extra row tells us whether another page exists, without a second COUNT query.
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
            return await _context.BlogPosts
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new PostDetailDto
                {
                    Id = p.Id,
                    Title = p.Title,
                    Content = p.Content,
                    PublishedDate = p.PublishedDate,
                    AuthorName = _context.Users
                        .Where(u => u.Id == p.AuthorId)
                        .Select(u => u.UserName ?? "Unknown")
                        .FirstOrDefault() ?? "Unknown",
                    AuthorId = p.AuthorId,
                    Tags = p.Tags.ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);
        }

        public Task<BlogPost?> FindAsync(int id, CancellationToken cancellationToken = default) =>
            _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        public void Add(BlogPost post) => _context.BlogPosts.Add(post);

        public void Remove(BlogPost post) => _context.BlogPosts.Remove(post);

        /// <summary>
        /// Commits the change and its outbox rows together.
        ///
        /// The two writes have to share a transaction. Saving the post first and the events
        /// afterwards would leave a window in which a crash produces a published post that was
        /// never announced - precisely the failure the outbox exists to prevent. Inside one
        /// transaction the database either has both or neither.
        ///
        /// The order still matters within that transaction: the post is saved first so that the
        /// identity column is populated, because the event carries the post id.
        /// </summary>
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // Captured before the save, because a deleted entity leaves the change tracker once
            // the save completes and its event would otherwise be lost.
            var sources = _context.CaptureEventSources();

            if (sources.Count == 0)
            {
                // Nothing to announce; no need to pay for an explicit transaction.
                return await _context.SaveChangesAsync(cancellationToken);
            }

            // Required when a retrying execution strategy is configured: the whole block has to
            // be replayable, not just the individual SaveChanges calls inside it.
            var strategy = _context.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                var written = await _context.SaveChangesAsync(cancellationToken);

                // Identities are populated by now, so the events carry real post ids.
                var events = ApplicationDbContext.DrainEvents(sources);

                await _events.DispatchAsync(events, cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                return written;
            });
        }

        /// <summary>
        /// Projected in SQL so only the columns the list needs come back. The author name is a
        /// correlated subquery rather than a navigation property, because the domain entity
        /// deliberately does not reference the Identity user.
        /// </summary>
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
                Tags = p.Tags.ToList()
            });

        internal static PostSummaryDto ToSummary(PostRow row) => new()
        {
            Id = row.Id,
            Title = row.Title,
            Excerpt = Excerpt.From(row.Content),
            PublishedDate = row.PublishedDate,
            AuthorName = row.AuthorName,
            Tags = row.Tags
        };

        internal sealed class PostRow
        {
            public int Id { get; init; }
            public string Title { get; init; } = string.Empty;
            public string Content { get; init; } = string.Empty;
            public DateTime PublishedDate { get; init; }
            public string AuthorName { get; init; } = string.Empty;
            public List<string> Tags { get; init; } = new();
        }
    }
}
