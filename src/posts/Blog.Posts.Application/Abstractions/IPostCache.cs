namespace Blog.Posts.Application.Abstractions;

/// <summary>
/// Cache-aside for post reads. Every failure path is swallowed by implementations: the cache
/// is an optimisation, and an unreachable Redis must degrade to slower reads, never to an error.
/// </summary>
public interface IPostCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Current cache generation. It is part of every key, so bumping it orphans all previously
    /// cached posts at once without SCAN or KEYS (both O(n) against Redis).
    /// </summary>
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a new generation, invalidating every cached post and page.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
