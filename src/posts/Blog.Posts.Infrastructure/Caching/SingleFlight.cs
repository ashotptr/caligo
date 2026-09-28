using System.Collections.Concurrent;

namespace Blog.Posts.Infrastructure.Caching
{
    // Collapses concurrent loads of the same key into one. The first caller (the leader) runs the
    // work; callers that arrive while it runs await the same task. The leader always waits for the
    // work to finish because the work may be using its request-scoped DbContext; followers may stop
    // waiting when their own request is cancelled.
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
