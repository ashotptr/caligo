using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Blog.Analytics.Core.Views;

namespace Blog.Analytics.Infrastructure.Stores
{
    public class MongoViewStore : IViewStore
    {
        private const int DuplicateKeyError = 11000;

        private readonly IMongoCollection<PostView>? _views;
        private readonly ILogger<MongoViewStore> _logger;

        public MongoViewStore(IConfiguration configuration, ILogger<MongoViewStore> logger)
        {
            _logger = logger;

            var connectionString = configuration.GetConnectionString("Mongo");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            try
            {
                var settings = MongoClientSettings.FromConnectionString(connectionString);
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
                settings.ConnectTimeout = TimeSpan.FromSeconds(5);

                var database = new MongoClient(settings)
                    .GetDatabase(configuration.GetValue("Analytics:MongoDatabase", "blog_analytics"));

                _views = database.GetCollection<PostView>("post_views");

                EnsureIndexes();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mongo unavailable; analytics disabled.");
                _views = null;
            }
        }

        public bool IsConfigured => _views is not null;

        private void EnsureIndexes()
        {
            if (_views is null)
            {
                return;
            }

            try
            {
                _views.Indexes.CreateMany(new[]
                {
                    // Supports the aggregations behind both analytics endpoints.
                    new CreateIndexModel<PostView>(
                        Builders<PostView>.IndexKeys.Ascending(v => v.ViewedAt).Ascending(v => v.PostId)),

                    // The idempotency guarantee itself. Deduplicating in application code would
                    // race between two consumer instances; a unique index cannot.
                    new CreateIndexModel<PostView>(
                        Builders<PostView>.IndexKeys.Ascending(v => v.EventId),
                        new CreateIndexOptions { Unique = true, Name = "ux_event_id" })
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create analytics indexes.");
            }
        }

        public async Task RecordAsync(PostView view, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return;
            }

            try
            {
                await _views.InsertOneAsync(view, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Code == DuplicateKeyError)
            {
                // Already recorded. A replayed event is the expected case, not a failure.
            }
        }

        public async Task<int> RecordManyAsync(
            IReadOnlyList<PostView> views, CancellationToken cancellationToken = default)
        {
            if (_views is null || views.Count == 0)
            {
                return 0;
            }

            try
            {
                // Unordered so one duplicate does not abandon the rest of the batch; without it
                // Mongo stops at the first error and the remaining views are silently skipped.
                await _views.InsertManyAsync(
                    views,
                    new InsertManyOptions { IsOrdered = false },
                    cancellationToken);

                return views.Count;
            }
            catch (MongoBulkWriteException<PostView> ex)
            {
                var duplicates = ex.WriteErrors.Count(e => e.Code == DuplicateKeyError);

                if (duplicates != ex.WriteErrors.Count)
                {
                    // Something other than a replay went wrong; let the caller retry the batch.
                    throw;
                }

                var inserted = views.Count - duplicates;

                _logger.LogDebug(
                    "Recorded {Inserted} view(s); {Duplicates} had already been processed.", inserted, duplicates);

                return inserted;
            }
        }

        public async Task<IReadOnlyList<PostStats>> TopPostsAsync(
            int days, int limit, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return Array.Empty<PostStats>();
            }

            var since = DateTime.UtcNow.AddDays(-days);

            var results = await _views.Aggregate()
                .Match(v => v.ViewedAt >= since)
                .Group(v => v.PostId, g => new
                {
                    PostId = g.Key,
                    Views = g.Count(),
                    Visitors = g.Select(v => v.VisitorHash).Distinct().Count()
                })
                .SortByDescending(g => g.Views)
                .Limit(limit)
                .ToListAsync(cancellationToken);

            return results
                .Select(r => new PostStats(r.PostId, r.Views, r.Visitors))
                .ToList();
        }

        public async Task<IReadOnlyList<PostStats>> ForPostsAsync(
            IReadOnlyList<int> postIds, CancellationToken cancellationToken = default)
        {
            if (_views is null || postIds.Count == 0)
            {
                return Array.Empty<PostStats>();
            }

            // One aggregation for the whole page. Calling ForPostAsync per post would turn a
            // twenty-item listing into twenty round trips to MongoDB.
            var results = await _views.Aggregate()
                .Match(v => postIds.Contains(v.PostId))
                .Group(v => v.PostId, g => new
                {
                    PostId = g.Key,
                    Views = g.Count(),
                    Visitors = g.Select(v => v.VisitorHash).Distinct().Count()
                })
                .ToListAsync(cancellationToken);

            var found = results.ToDictionary(r => r.PostId, r => new PostStats(r.PostId, r.Views, r.Visitors));

            // Posts with no views still get an entry, so the caller can zip the result against
            // its request without checking for gaps.
            return postIds.Select(id => found.TryGetValue(id, out var stats) ? stats : PostStats.Empty(id)).ToList();
        }

        public async Task<PostStats> ForPostAsync(int postId, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return PostStats.Empty(postId);
            }

            var results = await _views.Aggregate()
                .Match(v => v.PostId == postId)
                .Group(v => v.PostId, g => new
                {
                    PostId = g.Key,
                    Views = g.Count(),
                    Visitors = g.Select(v => v.VisitorHash).Distinct().Count()
                })
                .ToListAsync(cancellationToken);

            var first = results.FirstOrDefault();

            return first is null
                ? PostStats.Empty(postId)
                : new PostStats(first.PostId, first.Views, first.Visitors);
        }
    }
}
