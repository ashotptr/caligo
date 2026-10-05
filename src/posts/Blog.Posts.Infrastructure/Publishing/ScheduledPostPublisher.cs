using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Persistence;

namespace Blog.Posts.Infrastructure.Publishing
{
    public class ScheduledPublishingOptions
    {
        public const string SectionName = "Posts";

        public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(1);

        public int SchedulerBatchSize { get; set; } = 100;
    }

    public class ScheduledPostPublisher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly TimeProvider _time;
        private readonly ScheduledPublishingOptions _options;
        private readonly ILogger<ScheduledPostPublisher> _logger;

        public ScheduledPostPublisher(
            IServiceScopeFactory scopes,
            TimeProvider time,
            IOptions<ScheduledPublishingOptions> options,
            ILogger<ScheduledPostPublisher> logger)
        {
            _scopes = scopes;
            _time = time;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.SchedulerInterval <= TimeSpan.Zero)
            {
                _logger.LogInformation("Posts:SchedulerInterval is zero; scheduled posts are published only on demand.");

                return;
            }

            _logger.LogInformation(
                "Scheduled publisher started: batches of {BatchSize}, polling every {Interval}.",
                _options.SchedulerBatchSize, _options.SchedulerInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                var claimed = 0;

                try
                {
                    claimed = await PublishDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled publishing pass failed; retrying.");
                }

                if (claimed < _options.SchedulerBatchSize)
                {
                    try
                    {
                        await Task.Delay(_options.SchedulerInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        public async Task<int> PublishDueAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var repository = scope.ServiceProvider.GetRequiredService<IPostRepository>();

            var now = _time.GetUtcNow().UtcDateTime;

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var due = await ClaimDueAsync(context, now, cancellationToken);

            if (due.Count == 0)
            {
                return 0;
            }

            var published = due.Count(post => post.PublishDue(now));

            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await scope.ServiceProvider.GetRequiredService<IPostCache>()
                .InvalidateAsync(PostService.ListScope, cancellationToken);

            _logger.LogInformation("Published {Count} scheduled post(s).", published);

            return due.Count;
        }

        private async Task<List<BlogPost>> ClaimDueAsync(
            ApplicationDbContext context, DateTime now, CancellationToken cancellationToken)
        {
            if (context.Database.IsNpgsql())
            {
                return await context.BlogPosts
                    .FromSqlRaw(
                        """
                        SELECT * FROM "BlogPosts"
                        WHERE "Status" = 1 AND "ScheduledFor" <= {0}
                        ORDER BY "ScheduledFor", "Id"
                        LIMIT {1}
                        FOR UPDATE SKIP LOCKED
                        """,
                        now,
                        _options.SchedulerBatchSize)
                    .ToListAsync(cancellationToken);
            }

            return await context.BlogPosts
                .Where(p => p.Status == PostStatus.Scheduled && p.ScheduledFor <= now)
                .OrderBy(p => p.ScheduledFor)
                .ThenBy(p => p.Id)
                .Take(_options.SchedulerBatchSize)
                .ToListAsync(cancellationToken);
        }
    }
}
