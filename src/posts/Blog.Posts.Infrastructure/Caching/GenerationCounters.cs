using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Blog.Posts.Infrastructure.Caching
{
    public interface IGenerationCounters
    {
        Task<long> CurrentAsync(string scope);

        Task BumpAsync(string scope);
    }

    public class RedisGenerationCounters : IGenerationCounters
    {
        private readonly IConnectionMultiplexer _redis;

        public RedisGenerationCounters(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<long> CurrentAsync(string scope)
        {
            var value = await _redis.GetDatabase().StringGetAsync(Key(scope));

            return value.TryParse(out long generation) ? generation : 0;
        }

        public Task BumpAsync(string scope) => _redis.GetDatabase().StringIncrementAsync(Key(scope));

        private static RedisKey Key(string scope) => $"Blog_gen:{scope}";
    }

    public class LocalGenerationCounters : IGenerationCounters
    {
        private readonly ConcurrentDictionary<string, long> _generations = new();

        public Task<long> CurrentAsync(string scope) =>
            Task.FromResult(_generations.TryGetValue(scope, out var generation) ? generation : 0);

        public Task BumpAsync(string scope)
        {
            _generations.AddOrUpdate(scope, 1, (_, generation) => generation + 1);

            return Task.CompletedTask;
        }
    }
}
