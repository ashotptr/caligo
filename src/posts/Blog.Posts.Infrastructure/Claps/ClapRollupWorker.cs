using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Claps
{
    public class ClapOptions
    {
        public const string SectionName = "Claps";

        public TimeSpan RollupInterval { get; set; } = TimeSpan.FromSeconds(1);

        public int RollupBatchSize { get; set; } = 5000;
    }

    // Folds the clap queue into the per-post totals, the way a sportsbook settles a finished match:
    // many small writes accepted cheaply at request time, applied in bulk afterwards. Every API
    // replica runs this loop; SKIP LOCKED hands each one a disjoint batch, so they drain a spike in
    // parallel without folding any delta twice.
    public class ClapRollupWorker : BackgroundService
    {
        private const int MaxAttempts = 5;

        private readonly IServiceScopeFactory _scopes;
        private readonly ClapOptions _options;
        private readonly ILogger<ClapRollupWorker> _logger;

        public ClapRollupWorker(
            IServiceScopeFactory scopes, IOptions<ClapOptions> options, ILogger<ClapRollupWorker> logger)
        {
            _scopes = scopes;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!UsesPostgres())
            {
                _logger.LogInformation("Not running on PostgreSQL; claps are applied directly and no rollup runs.");

                return;
            }

            _logger.LogInformation(
                "Clap rollup started: batches of {BatchSize}, idle interval {Interval}.",
                _options.RollupBatchSize, _options.RollupInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                var folded = 0;

                try
                {
                    folded = await FoldBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Clap rollup pass failed; retrying.");
                }

                if (folded < _options.RollupBatchSize)
                {
                    try
                    {
                        await Task.Delay(_options.RollupInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        // Returns how many deltas were folded. Public so tests and load tests can drain the queue.
        public async Task<int> FoldBatchAsync(CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await FoldOnceAsync(cancellationToken);
                }
                catch (Exception ex) when (
                    attempt < MaxAttempts &&
                    (PostgresConflicts.IsRetryable(ex) || PostgresConflicts.IsForeignKeyViolation(ex)))
                {
                    _logger.LogDebug(ex, "Clap rollup collided (attempt {Attempt}); retrying.", attempt);

                    await Task.Delay(PostgresConflicts.Backoff(attempt), cancellationToken);
                }
            }
        }

        private async Task<int> FoldOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopes.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // One statement, one transaction:
            //  - claim the oldest deltas nobody else holds (SKIP LOCKED) and delete them;
            //  - drop deltas whose post was deleted meanwhile;
            //  - add each post's sum to its total. Input is sorted by post id, so two replicas
            //    always lock totals rows in the same order and cannot deadlock each other.
            // If a post is deleted between the EXISTS check and the insert, the foreign key rejects
            // the statement, it rolls back as a whole, and the retry drops that post's deltas.
            var result = await context.Database.SqlQuery<FoldResult>($"""
                WITH batch AS (
                    DELETE FROM clap_deltas
                    WHERE "Id" IN (
                        SELECT "Id" FROM clap_deltas
                        ORDER BY "Id"
                        LIMIT {_options.RollupBatchSize}
                        FOR UPDATE SKIP LOCKED)
                    RETURNING "PostId", "Delta", "FirstClap"
                ), per_post AS (
                    SELECT b."PostId",
                           sum(b."Delta")::bigint AS "Claps",
                           count(*) FILTER (WHERE b."FirstClap") AS "Clappers"
                    FROM batch b
                    WHERE EXISTS (SELECT 1 FROM "BlogPosts" p WHERE p."Id" = b."PostId")
                    GROUP BY b."PostId"
                ), folded AS (
                    INSERT INTO post_clap_totals AS t ("PostId", "Claps", "Clappers", "UpdatedAt")
                    SELECT "PostId", "Claps", "Clappers", now()
                    FROM per_post
                    ORDER BY "PostId"
                    ON CONFLICT ("PostId") DO UPDATE
                        SET "Claps" = t."Claps" + EXCLUDED."Claps",
                            "Clappers" = t."Clappers" + EXCLUDED."Clappers",
                            "UpdatedAt" = now()
                    RETURNING 1
                )
                SELECT (SELECT count(*) FROM batch)::int AS "Deltas",
                       (SELECT count(*) FROM folded)::int AS "Posts"
                """).ToListAsync(cancellationToken);

            var folded = result.Count == 0 ? new FoldResult() : result[0];

            if (folded.Deltas > 0)
            {
                _logger.LogDebug("Folded {Deltas} clap delta(s) into {Posts} post total(s).", folded.Deltas, folded.Posts);
            }

            return folded.Deltas;
        }

        private bool UsesPostgres()
        {
            using var scope = _scopes.CreateScope();

            return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.IsNpgsql();
        }

        internal sealed class FoldResult
        {
            public int Deltas { get; set; }

            public int Posts { get; set; }
        }
    }
}
