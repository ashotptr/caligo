using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Caching
{
    public class DistributedPostCache : IPostCache
    {
        private const string VersionKey = "posts:version";

        private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(30);

        private readonly IDistributedCache _cache;
        private readonly ILogger<DistributedPostCache> _logger;

        public DistributedPostCache(IDistributedCache cache, ILogger<DistributedPostCache> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            try
            {
                var payload = await _cache.GetStringAsync(key, cancellationToken);

                return payload is null ? null : JsonSerializer.Deserialize<T>(payload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache read failed for {CacheKey}", key);

                return null;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            try
            {
                await _cache.SetStringAsync(
                    key,
                    JsonSerializer.Serialize(value),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache write failed for {CacheKey}", key);
            }
        }

        public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var version = await _cache.GetStringAsync(VersionKey, cancellationToken);

                if (version is not null)
                {
                    return version;
                }

                version = NewVersion();

                await _cache.SetStringAsync(
                    VersionKey,
                    version,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VersionTtl },
                    cancellationToken);

                return version;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache version read failed; serving uncached.");

                return NewVersion();
            }
        }

        public async Task InvalidateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _cache.SetStringAsync(
                    VersionKey,
                    NewVersion(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VersionTtl },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache invalidation failed; entries will expire on their TTL.");
            }
        }

        private static string NewVersion() => Guid.NewGuid().ToString("N")[..8];
    }
}
