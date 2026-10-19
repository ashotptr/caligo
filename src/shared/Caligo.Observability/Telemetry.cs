using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Caligo.Observability;

public static class CaligoActivitySources
{
    public const string Messaging = "Caligo.Messaging";

    public static readonly ActivitySource Kafka = new(Messaging);
}

public static class Telemetry
{
    public static IHostApplicationBuilder AddCaligoTelemetry(
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
            return builder;
        }

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                serviceInstanceId: Environment.MachineName)
            .AddEnvironmentVariableDetector();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");

                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource(CaligoActivitySources.Messaging);

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
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId;
        });

        if (!structuredStdout)
        {
            return;
        }

        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
    }
}
