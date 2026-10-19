using Microsoft.EntityFrameworkCore;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Domain.Posts;
using Caligo.Posts.Infrastructure.Persistence;

namespace Caligo.Posts.Infrastructure.Claps
{
    public class EfClapStore : IClapStore
    {
        public EfClapStore(ApplicationDbContext context, TimeProvider time)
        {
            Context = context;
            Time = time;
        }

        protected ApplicationDbContext Context { get; }

        protected TimeProvider Time { get; }

        public virtual async Task<ClapOutcome?> ClapAsync(
            int postId, string userId, int count, CancellationToken cancellationToken = default)
        {
            await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);

            if (!await Context.BlogPosts.AnyAsync(p => p.Id == postId, cancellationToken))
            {
                return null;
            }

            var now = Time.GetUtcNow().UtcDateTime;

            var clap = await Context.PostClaps
                .FirstOrDefaultAsync(c => c.PostId == postId && c.UserId == userId, cancellationToken);

            var before = clap?.Claps ?? 0;
            var applied = Math.Min(count, ClapRules.MaxPerReader - before);

            if (applied <= 0)
            {
                return new ClapOutcome(0, before, false);
            }

            if (clap is null)
            {
                clap = new PostClap { PostId = postId, UserId = userId };

                Context.PostClaps.Add(clap);
            }

            clap.Claps = before + applied;
            clap.LastApplied = applied;
            clap.UpdatedAt = now;

            var total = await Context.PostClapTotals.FirstOrDefaultAsync(t => t.PostId == postId, cancellationToken);

            if (total is null)
            {
                total = new PostClapTotal { PostId = postId };

                Context.PostClapTotals.Add(total);
            }

            total.Claps += applied;
            total.Clappers += before == 0 ? 1 : 0;
            total.UpdatedAt = now;

            await Context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ClapOutcome(applied, before + applied, before == 0);
        }

        public async Task<ClapTally> TallyAsync(int postId, string? userId, CancellationToken cancellationToken = default)
        {
            var total = await Context.PostClapTotals
                .AsNoTracking()
                .Where(t => t.PostId == postId)
                .Select(t => new { t.Claps, t.Clappers })
                .FirstOrDefaultAsync(cancellationToken);

            var mine = userId is null
                ? 0
                : await Context.PostClaps
                    .AsNoTracking()
                    .Where(c => c.PostId == postId && c.UserId == userId)
                    .Select(c => c.Claps)
                    .FirstOrDefaultAsync(cancellationToken);

            return new ClapTally(total?.Claps ?? 0, total?.Clappers ?? 0, mine);
        }

        public virtual async Task<ClapAudit> AuditAsync(int? postId, CancellationToken cancellationToken = default)
        {
            await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);

            var audit = await CollectAuditAsync(postId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return audit;
        }

        protected async Task<ClapAudit> CollectAuditAsync(int? postId, CancellationToken cancellationToken)
        {
            var expected = await Context.PostClaps
                .AsNoTracking()
                .Where(c => postId == null || c.PostId == postId)
                .GroupBy(c => c.PostId)
                .Select(g => new { PostId = g.Key, Claps = g.Sum(c => (long)c.Claps) })
                .ToDictionaryAsync(x => x.PostId, x => x.Claps, cancellationToken);

            var rolledUp = await Context.PostClapTotals
                .AsNoTracking()
                .Where(t => postId == null || t.PostId == postId)
                .ToDictionaryAsync(t => t.PostId, t => t.Claps, cancellationToken);

            var pending = await Context.ClapDeltas
                .AsNoTracking()
                .Where(d => postId == null || d.PostId == postId)
                .GroupBy(d => d.PostId)
                .Select(g => new { PostId = g.Key, Claps = g.Sum(d => (long)d.Delta) })
                .ToDictionaryAsync(x => x.PostId, x => x.Claps, cancellationToken);

            var posts = expected.Keys.Union(rolledUp.Keys).Union(pending.Keys).ToList();

            var mismatches = posts
                .Select(id => new ClapMismatch(
                    id,
                    expected.GetValueOrDefault(id),
                    rolledUp.GetValueOrDefault(id),
                    pending.GetValueOrDefault(id)))
                .Where(m => m.Expected != m.RolledUp + m.Pending)
                .OrderBy(m => m.PostId)
                .ToList();

            return new ClapAudit(
                posts.Count,
                expected.Values.Sum(),
                rolledUp.Values.Sum(),
                pending.Values.Sum(),
                mismatches);
        }
    }
}
