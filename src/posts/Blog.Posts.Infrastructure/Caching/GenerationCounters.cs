using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Blog.Posts.Infrastructure.Caching
{
    // A generation per cache scope (all list pages, or one post). Cache keys embed the generation, so
    // bumping it retires every entry in the scope, and a slow reader that loaded before the bump
    // writes its stale copy under the old generation, where nobody looks any more.
    public interface IGenerationCounters
    {
        Task<long> CurrentAsync(string scope);

        Task BumpAsync(string scope);
    }

    // INCR is atomic and readers never create the key, so there is no get-then-set window in which
    // two replicas settle on different generations.
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
