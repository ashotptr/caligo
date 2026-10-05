using System.Collections.Concurrent;

namespace Blog.Posts.Infrastructure.Caching
{
    public sealed class SingleFlight
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _flights = new();

        public int InFlight => _flights.Count;

        public async Task<T?> RunAsync<T>(string key, Func<Task<T?>> work, CancellationToken cancellationToken)
            where T : class
        {
            var mine = new Lazy<Task<object?>>(
                async () => await work().ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication);

            var flight = _flights.GetOrAdd(key, mine);

            if (!ReferenceEquals(flight, mine))
            {
                return (T?)await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return (T?)await flight.Value.ConfigureAwait(false);
            }
            finally
            {
                _flights.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, flight));
            }
        }
    }
}
