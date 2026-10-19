using Microsoft.Extensions.Options;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Infrastructure;
using Blog.Posts.Application.Abstractions;

namespace Blog.Api.Composition;

public class CatalogContentLookup : IContentLookup
{
    private static readonly IReadOnlyDictionary<int, string> NoTitles = new Dictionary<int, string>();

    private readonly ICatalogReader _catalog;
    private readonly CatalogOptions _options;

    public CatalogContentLookup(ICatalogReader catalog, IOptions<CatalogOptions> options)
    {
        _catalog = catalog;
        _options = options.Value;
    }

    public Task<bool> ExistsAsync(int contentId, CancellationToken cancellationToken = default) =>
        _options.Enabled
            ? _catalog.ContentExistsAsync(contentId, cancellationToken)
            : Task.FromResult(false);

    public Task<IReadOnlyDictionary<int, string>> GetTitlesAsync(
        IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken = default) =>
        _options.Enabled
            ? _catalog.GetTitlesAsync(contentIds, cancellationToken)
            : Task.FromResult(NoTitles);
}
