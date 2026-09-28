using Microsoft.EntityFrameworkCore;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Search
{
    public class SqlitePostSearch : IPostSearch
    {
        private readonly ApplicationDbContext _context;

        public SqlitePostSearch(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<PostSummaryDto>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken = default)
        {
            var pattern = $"%{query}%";

            var rows = await _context.BlogPosts
                .AsNoTracking()
                .Where(p => p.Status == PostStatus.Published)
                .Where(p => EF.Functions.Like(p.Title, pattern) || EF.Functions.Like(p.Content, pattern))
                .OrderByDescending(p => p.PublishedDate)
                .ThenByDescending(p => p.Id)
                .Take(limit)
                .Select(p => new EfPostRepository.PostRow
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
                    ScheduledFor = p.ScheduledFor
                })
                .ToListAsync(cancellationToken);

            return rows.Select(EfPostRepository.ToSummary).ToList();
        }
    }
}
