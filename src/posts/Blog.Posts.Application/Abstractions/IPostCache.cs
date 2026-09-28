namespace Blog.Posts.Application.Abstractions;

public interface IPostCache
{
    // Returns the cached value, or runs the loader once per key however many callers miss at the
    // same moment. The loaded value is stored only if shouldCache accepts it.
    Task<T?> GetOrLoadAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> load,
        Func<T, bool> shouldCache,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class;

    // Keys embed their scope's generation; invalidating a scope bumps it, retiring every key built
    // from the old one.
    Task<long> GetGenerationAsync(string scope, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string scope, CancellationToken cancellationToken = default);
}
