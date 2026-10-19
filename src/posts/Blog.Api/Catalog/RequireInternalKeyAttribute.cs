using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Blog.Catalog.Application.Media;

namespace Blog.Api.Catalog;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var rules = context.HttpContext.RequestServices.GetRequiredService<MediaRules>();

        if (!rules.Enabled)
        {
            context.Result = new ObjectResult(RequireMediaAttribute.SwitchedOff)
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };

            return;
        }

        var presented = context.HttpContext.Request.Headers[InternalKeys.Header].ToString();

        if (!InternalKeys.Matches(rules.SigningKey, presented))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
        }
    }
}
