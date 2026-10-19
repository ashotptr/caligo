using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Blog.Catalog.Infrastructure;

namespace Blog.Api.Catalog;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireCatalogAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<CatalogOptions>>().Value;

        if (!options.Enabled)
        {
            context.Result = new ObjectResult("The catalog needs PostgreSQL and is switched off on this server.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
