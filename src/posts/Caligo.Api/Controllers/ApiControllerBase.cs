using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Caligo.SharedKernel;

namespace Caligo.Api.Controllers;

public abstract class ApiControllerBase : ControllerBase
{
    protected Caller CurrentCaller() =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier), User.IsInRole("Admin"));

    protected static string ETag(int version) => $"\"{version}\"";

    protected int? ExpectedVersion()
    {
        var header = Request.Headers.IfMatch.ToString().Trim();

        if (header.Length == 0 || header == "*")
        {
            return null;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        return int.TryParse(header.Trim('"'), out var version) ? version : -1;
    }

    protected IActionResult Respond<T>(Result<T> result) => result.Status switch
    {
        ResultStatus.Ok => Ok(result.Value),
        _ => Failure(result.Status, result.Error)
    };

    protected IActionResult Respond(Result result) => result.Status switch
    {
        ResultStatus.Ok => NoContent(),
        _ => Failure(result.Status, result.Error)
    };

    protected IActionResult Failure(ResultStatus status, string? error) => status switch
    {
        ResultStatus.NotFound => NotFound(),
        ResultStatus.Forbidden => error is null ? Forbid() : StatusCode(StatusCodes.Status403Forbidden, error),
        ResultStatus.Unauthorized => Unauthorized(),
        ResultStatus.Invalid => BadRequest(error),
        ResultStatus.Conflict => Conflict(error),
        ResultStatus.PreconditionFailed => StatusCode(StatusCodes.Status412PreconditionFailed, error),
        ResultStatus.Gone => StatusCode(StatusCodes.Status410Gone, error),
        ResultStatus.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, error),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };
}
