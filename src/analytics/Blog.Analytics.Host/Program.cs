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

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, endpoint => endpoint.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(8081, endpoint => endpoint.Protocols = HttpProtocols.Http1);
});

builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.SectionName);

builder.Services.AddSingleton<IViewStore, MongoViewStore>();
builder.Services.AddSingleton<ITrendingStore, RedisTrendingStore>();

builder.Services.AddHostedService<RawViewConsumer>();
builder.Services.AddHostedService<TrendingConsumer>();

var app = builder.Build();

app.MapGrpcService<AnalyticsReaderService>();

app.MapBlogHealthChecks();

app.MapGet("/", () => Results.Text(
    "Blog.Analytics speaks gRPC.",
    "text/plain"));

app.Logger.LogInformation(
    "Analytics service starting. Mongo configured: {Mongo}. Redis configured: {Redis}.",
    app.Services.GetRequiredService<IViewStore>().IsConfigured,
    app.Services.GetRequiredService<ITrendingStore>().IsConfigured);

app.Run();

namespace Blog.Analytics.Host
{
    public sealed class AnalyticsHostMarker;
}
