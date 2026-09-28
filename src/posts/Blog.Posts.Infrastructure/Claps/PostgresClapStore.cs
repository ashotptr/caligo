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
            // One statement at READ COMMITTED, no read-then-write:
            //  - ON CONFLICT DO UPDATE locks the reader's row and always works on its latest committed
            //    version, so two racing requests from one reader are applied one after the other.
            //  - The right-hand side of SET reads the row as it was before this update, so
            //    LastApplied is exactly what this request added after the cap, and never more.
            //  - The WHERE makes a capped reader a no-op instead of an update.
            //  - The accepted delta is queued in the same statement, so the count and the queue can
            //    never disagree. Nothing touches the post's total here: that row would be the hot
            //    spot of a viral post, so ClapRollupWorker folds the queue into it in batches.
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
            // The audit is three queries (per-reader sums, rolled-up totals, pending deltas) that must
            // describe one moment. At READ COMMITTED each query takes its own snapshot: a rollup that
            // commits between the second and third moves claps out of the queue after the totals were
            // read, and the audit reports a mismatch that never existed. REPEATABLE READ runs all three
            // on one snapshot without blocking writers. READ ONLY lets Postgres skip write bookkeeping.
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
