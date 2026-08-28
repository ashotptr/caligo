using Microsoft.AspNetCore.Server.Kestrel.Core;
using Blog.Analytics.Host;
using Blog.Observability;
using OpenTelemetry.Trace;
using Blog.Analytics.Core.Views;
using Blog.Analytics.Host.Services;
using Blog.Analytics.Infrastructure.Messaging;
using Blog.Analytics.Infrastructure.Stores;

var builder = WebApplication.CreateBuilder(args);

builder.AddBlogTelemetry("blog-analytics", tracing => tracing.AddRedisInstrumentation());

builder.Services.AddHealthChecks()
    .AddCheck(
        "mongo",
        new MongoHealthCheck(
            builder.Configuration.GetConnectionString("Mongo"),
            builder.Configuration.GetValue("Analytics:MongoDatabase", "blog_analytics")!),
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddRedis(
        _ => builder.Configuration.GetConnectionString("Redis") ?? string.Empty,
        name: "redis",
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "kafka",
        new KafkaBrokerHealthCheck(builder.Configuration["Kafka:BootstrapServers"] ?? string.Empty),
        tags: new[] { HealthEndpoints.ReadinessTag });

// gRPC needs HTTP/2. Inside the cluster there is no TLS to negotiate the protocol with, so the
// endpoint is told to speak h2c (cleartext HTTP/2) rather than trying to upgrade from 1.1.
//
// Two endpoints, because they cannot share a protocol. gRPC over cleartext needs HTTP/2 with
// prior knowledge - there is no TLS here to negotiate it through ALPN - while Kubernetes
// probes and anything else speak HTTP/1.1. Serving both on one port would mean guessing.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, endpoint => endpoint.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(8081, endpoint => endpoint.Protocols = HttpProtocols.Http1);
});

builder.Services.AddGrpc(options =>
{
    // Surfaces the real exception message to the caller. Acceptable because this endpoint is
    // internal only - it is never exposed through the ingress.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.SectionName);

// Both stores are singletons: each owns a client with its own connection pool, and creating
// one per request would defeat the pooling entirely.
builder.Services.AddSingleton<IViewStore, MongoViewStore>();
builder.Services.AddSingleton<ITrendingStore, RedisTrendingStore>();

// Two consumer groups over one topic. Neither knows the other exists; each keeps its own
// offsets and can be rebuilt from the log independently.
builder.Services.AddHostedService<RawViewConsumer>();
builder.Services.AddHostedService<TrendingConsumer>();

var app = builder.Build();

app.MapGrpcService<AnalyticsReaderService>();

app.MapBlogHealthChecks();

// Plain HTTP/1.1 probes would fail against an HTTP/2-only endpoint, so readiness is reported
// through gRPC's own health protocol instead of a REST endpoint.
app.MapGet("/", () => Results.Text(
    "Blog.Analytics speaks gRPC. See Protos/analytics.proto for the contract.",
    "text/plain"));

app.Logger.LogInformation(
    "Analytics service starting. Mongo configured: {Mongo}. Redis configured: {Redis}.",
    app.Services.GetRequiredService<IViewStore>().IsConfigured,
    app.Services.GetRequiredService<ITrendingStore>().IsConfigured);

app.Run();

namespace Blog.Analytics.Host
{
    /// <summary>
    /// Lets a test locate this assembly's entry point. A second "public partial class Program"
    /// would collide with the one the posts API declares - top-level statements always put
    /// Program in the global namespace, so two referenced hosts cannot both have one.
    /// </summary>
    public sealed class AnalyticsHostMarker;
}
