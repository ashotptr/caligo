using System.Security.Claims;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Infrastructure.Engine;

namespace Caligo.Api.Catalog;

public class HttpCatalogViewer : ICatalogViewer
{
    private readonly IHttpContextAccessor _http;
    private readonly MixtapeVisibility _visibility;
    private Task<HiddenMusic>? _hidden;

    public HttpCatalogViewer(IHttpContextAccessor http, MixtapeVisibility visibility)
    {
        _http = http;
        _visibility = visibility;
    }

    public string? UserName
    {
        get
        {
            var user = _http.HttpContext?.User;

            return user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.Name) : null;
        }
    }

    public Task<HiddenMusic> HiddenAsync(CancellationToken cancellationToken = default) =>
        _hidden ??= _visibility.HiddenForAsync(UserName, cancellationToken);
}
