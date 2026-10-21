using System.Collections.Concurrent;
using Caligo.Catalog.Application.Abstractions;

namespace Caligo.Catalog.Infrastructure.Engine
{
    public class MixtapeVisibilityCache
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);
        private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly TimeProvider _time;

        public MixtapeVisibilityCache(TimeProvider time)
        {
            _time = time;
        }

        public async Task<IReadOnlySet<string>> VisibleAsync(
            string? user, ICaligoEngine engine, CancellationToken cancellationToken = default)
        {
            var key = user ?? string.Empty;
            var now = _time.GetUtcNow();

            if (_entries.TryGetValue(key, out var entry) && now - entry.At < Fresh)
            {
                return entry.Folders;
            }

            var fetched = await engine.VisibleMixtapeFoldersAsync(user, cancellationToken);

            if (fetched is not null)
            {
                _entries[key] = new Entry(fetched, now);

                return fetched;
            }

            if (entry is not null)
            {
                return entry.Folders;
            }

            return _entries.TryGetValue(string.Empty, out var everyone) ? everyone.Folders : Nothing;
        }

        public void Forget() => _entries.Clear();

        private sealed record Entry(IReadOnlySet<string> Folders, DateTimeOffset At);
    }
}
