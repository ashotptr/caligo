using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Blog.Analytics.Core.Views;

namespace Blog.Analytics.Infrastructure.Stores
{
    /// <summary>
    /// The trending leaderboard, kept in Redis sorted sets.
    ///
    /// One sorted set per hour, member = post id, score = views in that hour. Trending over the
    /// last N hours is then a ZUNIONSTORE across N small keys rather than an aggregation over
    /// every raw view in the window, and expiring a bucket is what makes old traffic fall out
    /// of the ranking without anything having to delete it.
    ///
    /// This is the second consumer group's projection of the same Kafka topic the raw writer
    /// reads. Neither knows about the other, and either can be rebuilt from the log alone.
    /// </summary>
    public class RedisTrendingStore : ITrendingStore
    {
        private const string KeyPrefix = "trending";

        /// <summary>
        /// The union is recomputed on every read - it spans at most 48 small sorted sets, which
        /// is cheaper than reasoning about staleness. This expiry exists only so the destination
        /// key does not outlive the request that produced it.
        /// </summary>
        private static readonly TimeSpan UnionKeyTtl = TimeSpan.FromMinutes(1);

        private readonly IConnectionMultiplexer? _redis;
        private readonly ILogger<RedisTrendingStore> _logger;

        public RedisTrendingStore(IConfiguration configuration, ILogger<RedisTrendingStore> logger)
        {
            _logger = logger;

            var connectionString = configuration.GetConnectionString("Redis");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            try
            {
                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 5000;

                _redis = ConnectionMultiplexer.Connect(options);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis unavailable; trending disabled.");
                _redis = null;
            }
        }

        public bool IsConfigured => _redis is not null;

        public Task IncrementAsync(int postId, DateTime viewedAt, CancellationToken cancellationToken = default) =>
            IncrementManyAsync(
                new[] { new PostView { PostId = postId, ViewedAt = viewedAt } }, cancellationToken);

        public async Task IncrementManyAsync(
            IReadOnlyList<PostView> views, CancellationToken cancellationToken = default)
        {
            if (_redis is null || views.Count == 0)
            {
                return;
            }

            try
            {
                var database = _redis.GetDatabase();

                // Collapse the batch in memory first: a hundred views of the same post in one
                // Kafka batch should be one ZINCRBY of 100, not a hundred round trips.
                var increments = views
                    .GroupBy(v => (Bucket: BucketKey(v.ViewedAt), v.PostId))
                    .Select(g => (g.Key.Bucket, g.Key.PostId, Count: (double)g.Count()));

                var batch = database.CreateBatch();
                var tasks = new List<Task>();

                foreach (var (bucket, postId, count) in increments)
                {
                    tasks.Add(batch.SortedSetIncrementAsync(bucket, postId, count));

                    // Buckets expire on their own, so nothing has to sweep them. The extra day
                    // of slack keeps a bucket alive slightly longer than the widest window that
                    // could still ask for it.
                    tasks.Add(batch.KeyExpireAsync(bucket, TimeSpan.FromHours(49)));
                }

                batch.Execute();

                await Task.WhenAll(tasks);
            }
            catch (RedisException ex)
            {
                // Trending is a disposable projection; losing an increment costs a little
                // accuracy on a leaderboard and nothing else.
                _logger.LogWarning(ex, "Could not update trending for {Count} view(s).", views.Count);
            }
        }

        public async Task<IReadOnlyList<TrendingEntry>> TopAsync(
            int windowHours, int limit, CancellationToken cancellationToken = default)
        {
            if (_redis is null)
            {
                return Array.Empty<TrendingEntry>();
            }

            windowHours = Math.Clamp(windowHours, 1, 48);
            limit = Math.Clamp(limit, 1, 100);

            try
            {
                var database = _redis.GetDatabase();

                var now = DateTime.UtcNow;

                var buckets = Enumerable.Range(0, windowHours)
                    .Select(hoursAgo => (RedisKey)BucketKey(now.AddHours(-hoursAgo)))
                    .ToArray();

                var destination = (RedisKey)$"{KeyPrefix}:union:{windowHours}h";

                // Redis does the union server-side; pulling every bucket back to add them up
                // here would move far more data than the answer is worth.
                await database.SortedSetCombineAndStoreAsync(SetOperation.Union, destination, buckets);
                await database.KeyExpireAsync(destination, UnionKeyTtl);

                var top = await database.SortedSetRangeByRankWithScoresAsync(
                    destination, 0, limit - 1, Order.Descending);

                return top
                    .Where(entry => entry.Element.HasValue && (long)entry.Score > 0)
                    .Select(entry => new TrendingEntry((int)entry.Element, (long)entry.Score))
                    .ToList();
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Could not read trending.");

                return Array.Empty<TrendingEntry>();
            }
        }

        private static string BucketKey(DateTime instant) =>
            $"{KeyPrefix}:{instant.ToUniversalTime():yyyyMMddHH}";
    }
}
