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
                query = query.Where(p =>
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

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var sources = _context.CaptureEventSources();

            if (sources.Count == 0)
            {
                return await _context.SaveChangesAsync(cancellationToken);
            }

            var strategy = _context.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                var written = await _context.SaveChangesAsync(cancellationToken);

                var events = ApplicationDbContext.DrainEvents(sources);

                await _events.DispatchAsync(events, cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                return written;
            });
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
