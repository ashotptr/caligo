namespace Caligo.Posts.Application.Abstractions;

public interface IPostCache
{
    Task<T?> GetOrLoadAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> load,
        Func<T, bool> shouldCache,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class;

    Task<long> GetGenerationAsync(string scope, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string scope, CancellationToken cancellationToken = default);
}
