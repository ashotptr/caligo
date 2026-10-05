using StackExchange.Redis;

namespace Blog.Posts.Infrastructure.Caching
{
    public sealed class RedisConnection : IDisposable
    {
        public RedisConnection(IConnectionMultiplexer? multiplexer)
        {
            Multiplexer = multiplexer;
        }

        public IConnectionMultiplexer? Multiplexer { get; }

        public void Dispose() => Multiplexer?.Dispose();
    }
}
