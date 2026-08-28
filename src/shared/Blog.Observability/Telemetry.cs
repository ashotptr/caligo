using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Blog.Observability;

/// <summary>
/// The ActivitySources this codebase creates spans from. Named per concern rather than one
/// global source, because a source is the unit an operator can switch off.
/// </summary>
public static class BlogActivitySources
{
    public const string Messaging = "Blog.Messaging";

    public static readonly ActivitySource Kafka = new(Messaging);
}

public static class Telemetry
{
    /// <summary>
    /// Traces, metrics and logs for one service, all exported over OTLP.
    ///
    /// The point of doing this now rather than later: with three processes and two brokers in
    /// the path, "why was that request slow" stopped being answerable by reading one log file
    /// the moment the services were split. A trace that spans HTTP, the outbox, Kafka and gRPC
    /// is the only thing that puts the pieces back in order.
    /// </summary>
    public static IHostApplicationBuilder AddBlogTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var endpoint = builder.Configuration["Otlp:Endpoint"];

        var collectorConfigured = !string.IsNullOrWhiteSpace(endpoint);

        ConfigureLogging(builder, structuredStdout: collectorConfigured);

        if (!collectorConfigured)
        {
            // No collector configured: the service runs exactly as before, just unobserved.
            // Telemetry must never be the reason something cannot start.
            return builder;
        }

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0",

                // Distinguishes replicas of the same service, which is what makes "only one pod
                // is slow" visible instead of averaged away.
                serviceInstanceId: Environment.MachineName)
            .AddEnvironmentVariableDetector();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Health probes fire every few seconds and would otherwise be most of
                        // the trace volume while telling nobody anything.
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");

                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource(BlogActivitySources.Messaging);

                configureTracing?.Invoke(tracing);

                tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(endpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                configureMetrics?.Invoke(metrics);

                metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(endpoint));
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);

            // Without these the exported log record loses the structured arguments and keeps
            // only the rendered string, which cannot be queried.
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            logging.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(endpoint));
        });

        return builder;
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder, bool structuredStdout)
    {
        builder.Logging.Configure(options =>
        {
            // Stamps every log line with the trace and span it happened inside, which is what
            // lets a line found in `docker logs` be looked up as a trace.
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId;
        });

        if (!structuredStdout)
        {
            // Running without a collector means running locally, where the default console
            // formatter is far easier to read than a wall of JSON.
            return;
        }

        // Structured stdout alongside OTLP, so `docker logs` and a log shipper both get
        // something machine-readable. Serilog would do the same job, but this is the platform's
        // own formatter and adds no dependency to three services.
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
    }
}
