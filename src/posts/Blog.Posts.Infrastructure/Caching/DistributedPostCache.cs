using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Caching
{
    public class DistributedPostCache : IPostCache
    {
        private readonly IDistributedCache _cache;
        private readonly IGenerationCounters _generations;
        private readonly SingleFlight _flights;
        private readonly ILogger<DistributedPostCache> _logger;

        public DistributedPostCache(
            IDistributedCache cache,
            IGenerationCounters generations,
            SingleFlight flights,
            ILogger<DistributedPostCache> logger)
        {
            _cache = cache;
            _generations = generations;
            _flights = flights;
            _logger = logger;
        }

        public async Task<T?> GetOrLoadAsync<T>(
            string key,
            Func<CancellationToken, Task<T?>> load,
            Func<T, bool> shouldCache,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) where T : class
        {
            var cached = await ReadAsync<T>(key, cancellationToken);

            if (cached is not null)
            {
                return cached;
            }

            return await _flights.RunAsync(
                key,
                async () =>
                {
                    var filled = await ReadAsync<T>(key, CancellationToken.None);

                    if (filled is not null)
                    {
                        return filled;
                    }

                    var loaded = await load(CancellationToken.None);

                    if (loaded is not null && shouldCache(loaded))
                    {
                        await WriteAsync(key, loaded, Jitter(ttl));
                    }

                    return loaded;
                },
                cancellationToken);
        }

        public async Task<long> GetGenerationAsync(string scope, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _generations.CurrentAsync(scope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache generation read failed for {Scope}; serving uncached.", scope);

                return -Random.Shared.NextInt64(1, long.MaxValue);
            }
        }

        public async Task InvalidateAsync(string scope, CancellationToken cancellationToken = default)
        {
            try
            {
                await _generations.BumpAsync(scope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cache invalidation failed for {Scope}; entries will expire on their TTL.", scope);
            }
        }

        private async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken) where T : class
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

        private async Task WriteAsync<T>(string key, T value, TimeSpan ttl) where T : class
        {
            try
            {
                await _cache.SetStringAsync(
                    key,
                    JsonSerializer.Serialize(value),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cache write failed for {CacheKey}", key);
            }
        }

        private static TimeSpan Jitter(TimeSpan ttl) =>
            TimeSpan.FromTicks((long)(ttl.Ticks * (0.9 + Random.Shared.NextDouble() * 0.2)));
    }
}
