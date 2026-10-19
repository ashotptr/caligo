using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Application.Posts;
using Caligo.Posts.Domain.Posts;
using Caligo.Posts.Infrastructure.Persistence;

namespace Caligo.Posts.Infrastructure.Search
{
    public class PostgresPostSearch : IPostSearch
    {
        private const int CandidateWindow = 500;

        private const string TextSearchConfig = "english";

        private readonly ApplicationDbContext _context;

        public PostgresPostSearch(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<PostSummaryDto>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken = default)
        {
            var tag = query.Trim().ToLowerInvariant();

            var candidates = _context.BlogPosts
                .AsNoTracking()
                .Where(p => p.Status == PostStatus.Published)
                .Where(p => EF.Property<NpgsqlTsVector>(p, ApplicationDbContext.SearchVectorProperty)
                              .Matches(EF.Functions.PlainToTsQuery(TextSearchConfig, query))
                            || p.Tags.Contains(tag))
                .OrderByDescending(p => p.PublishedDate)
                .ThenByDescending(p => p.Id)
                .Take(CandidateWindow);

            var ranked = candidates
                .OrderByDescending(p => EF.Property<NpgsqlTsVector>(p, ApplicationDbContext.SearchVectorProperty)
                                          .Rank(EF.Functions.PlainToTsQuery(TextSearchConfig, query)))
                .ThenByDescending(p => p.PublishedDate)
                .ThenByDescending(p => p.Id)
                .Take(limit);

            var rows = await ranked
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
