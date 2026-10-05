using System.Data;
using Microsoft.EntityFrameworkCore;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Claps
{
    public class PostgresClapStore : EfClapStore
    {
        public PostgresClapStore(ApplicationDbContext context, TimeProvider time)
            : base(context, time)
        {
        }

        public override async Task<ClapOutcome?> ClapAsync(
            int postId, string userId, int count, CancellationToken cancellationToken = default)
        {
            List<AppliedRow> rows;

            try
            {
                rows = await Context.Database.SqlQuery<AppliedRow>($"""
                    WITH applied AS (
                        INSERT INTO post_claps AS c ("PostId", "UserId", "Claps", "LastApplied", "UpdatedAt")
                        VALUES ({postId}, {userId}, {count}, {count}, now())
                        ON CONFLICT ("PostId", "UserId") DO UPDATE
                            SET "Claps" = LEAST(c."Claps" + EXCLUDED."Claps", {ClapRules.MaxPerReader}),
                                "LastApplied" = LEAST(c."Claps" + EXCLUDED."Claps", {ClapRules.MaxPerReader}) - c."Claps",
                                "UpdatedAt" = now()
                            WHERE c."Claps" < {ClapRules.MaxPerReader}
                        RETURNING c."Claps", c."LastApplied"
                    ), queued AS (
                        INSERT INTO clap_deltas ("PostId", "UserId", "Delta", "FirstClap", "CreatedAt")
                        SELECT {postId}, {userId}, applied."LastApplied", applied."Claps" = applied."LastApplied", now()
                        FROM applied
                        WHERE applied."LastApplied" > 0
                    )
                    SELECT "Claps", "LastApplied" FROM applied
                    """).ToListAsync(cancellationToken);
            }
            catch (Exception ex) when (PostgresConflicts.IsForeignKeyViolation(ex))
            {
                return null;
            }

            if (rows.Count == 0)
            {
                var current = await Context.PostClaps
                    .AsNoTracking()
                    .Where(c => c.PostId == postId && c.UserId == userId)
                    .Select(c => c.Claps)
                    .FirstOrDefaultAsync(cancellationToken);

                return new ClapOutcome(0, current, false);
            }

            var row = rows[0];

            return new ClapOutcome(row.LastApplied, row.Claps, row.Claps == row.LastApplied);
        }

        public override async Task<ClapAudit> AuditAsync(int? postId, CancellationToken cancellationToken = default)
        {
            await using var transaction = await Context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);

            await Context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var audit = await CollectAuditAsync(postId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return audit;
        }

        internal sealed class AppliedRow
        {
            public int Claps { get; set; }

            public int LastApplied { get; set; }
        }
    }
}
