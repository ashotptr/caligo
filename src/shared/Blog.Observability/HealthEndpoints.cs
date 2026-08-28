using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blog.Observability;

public static class HealthEndpoints
{
    /// <summary>
    /// Dependency checks carry this tag; the liveness probe excludes them.
    /// </summary>
    public const string ReadinessTag = "ready";

    /// <summary>
    /// Maps the two probes Kubernetes actually distinguishes between.
    ///
    /// <c>/health/live</c> answers "is this process wedged", and deliberately checks nothing
    /// external. If it probed the database, a database blip would fail liveness, the kubelet
    /// would kill every pod, and a recoverable outage would become an outage plus a restart
    /// storm - the classic way this gets built wrong.
    ///
    /// <c>/health/ready</c> answers "can this instance serve traffic", and does check
    /// dependencies. Failing it removes the pod from the load balancer without killing it, so
    /// it rejoins by itself once the dependency returns.
    /// </summary>
    public static IEndpointRouteBuilder MapBlogHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponse
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteResponse
        });

        return endpoints;
    }

    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,

                // Surfaced because the first question after a failed probe is always which
                // dependency, and why.
                error = entry.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
    }
}
