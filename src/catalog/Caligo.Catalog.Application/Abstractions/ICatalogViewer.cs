using Caligo.Catalog.Application.Contents;

namespace Caligo.Catalog.Application.Abstractions;

public interface ICatalogViewer
{
    string? UserName { get; }

    Task<HiddenMusic> HiddenAsync(CancellationToken cancellationToken = default);
}
