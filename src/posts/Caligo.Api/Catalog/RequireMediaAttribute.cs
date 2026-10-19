using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Caligo.Catalog.Application.Media;

namespace Caligo.Api.Catalog;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMediaAttribute : Attribute, IResourceFilter
{
    public const string SwitchedOff = "On-demand video is switched off on this server.";

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var rules = context.HttpContext.RequestServices.GetRequiredService<MediaRules>();

        if (!rules.Enabled)
        {
            context.Result = new ObjectResult(SwitchedOff)
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
