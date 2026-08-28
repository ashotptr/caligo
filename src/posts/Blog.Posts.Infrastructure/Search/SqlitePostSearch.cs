using Microsoft.EntityFrameworkCore;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Search
{
    /// <summary>
    /// SQLite has no inverted index here, so this stays a scan. That is acceptable because
    /// SQLite is only ever the local-development and test provider; production runs Postgres.
    ///
    /// Tags are not searched: under SQLite they are stored as a JSON string by the value
    /// converter, so there is nothing to match against without parsing every row.
    /// </summary>
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
                    Tags = p.Tags.ToList()
                })
                .ToListAsync(cancellationToken);

            return rows.Select(EfPostRepository.ToSummary).ToList();
        }
    }
}
