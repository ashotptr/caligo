using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Caligo.Analytics.Core.Views;

namespace Caligo.Analytics.Infrastructure.Stores
{
    public class RedisTrendingStore : ITrendingStore
    {
        private const string KeyPrefix = "trending";

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

                var increments = views
                    .GroupBy(v => (Bucket: BucketKey(v.ViewedAt), v.PostId))
                    .Select(g => (g.Key.Bucket, g.Key.PostId, Count: (double)g.Count()));

                var batch = database.CreateBatch();
                var tasks = new List<Task>();

                foreach (var (bucket, postId, count) in increments)
                {
                    tasks.Add(batch.SortedSetIncrementAsync(bucket, postId, count));

                    tasks.Add(batch.KeyExpireAsync(bucket, TimeSpan.FromHours(49)));
                }

                batch.Execute();

                await Task.WhenAll(tasks);
            }
            catch (RedisException ex)
            {
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
