using Caligo.Posts.Application.Abstractions;

namespace Caligo.Posts.UnitTests;

internal sealed class FakePostCache : IPostCache
{
    private readonly Dictionary<string, object> _entries = new();
    private readonly Dictionary<string, long> _generations = new();

    public List<string> Loaded { get; } = new();

    public List<string> Invalidated { get; } = new();

    public int Count => _entries.Count;

    public void Seed<T>(string key, T value) where T : class => _entries[key] = value;

    public bool Contains(string key) => _entries.ContainsKey(key);

    public async Task<T?> GetOrLoadAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> load,
        Func<T, bool> shouldCache,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class
    {
        if (_entries.TryGetValue(key, out var cached))
        {
            return (T)cached;
        }

        Loaded.Add(key);

        var value = await load(cancellationToken);

        if (value is not null && shouldCache(value))
        {
            _entries[key] = value;
        }

        return value;
    }

    public Task<long> GetGenerationAsync(string scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(_generations.GetValueOrDefault(scope));

    public Task InvalidateAsync(string scope, CancellationToken cancellationToken = default)
    {
        Invalidated.Add(scope);

        _generations[scope] = _generations.GetValueOrDefault(scope) + 1;

        return Task.CompletedTask;
    }
}
