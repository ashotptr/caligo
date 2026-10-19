using Microsoft.AspNetCore.Server.Kestrel.Core;
using Caligo.Analytics.Host;
using Caligo.Observability;
using OpenTelemetry.Trace;
using Caligo.Analytics.Core.Olap;
using Caligo.Analytics.Core.Lab;
using Caligo.Analytics.Core.Playback;
using Caligo.Analytics.Core.Radio;
using Caligo.Analytics.Core.Views;
using Caligo.Analytics.Host.Services;
using Caligo.Analytics.Infrastructure.Messaging;
using Caligo.Analytics.Infrastructure.Radio;
using Caligo.Analytics.Infrastructure.Stores;

var builder = WebApplication.CreateBuilder(args);

builder.AddCaligoTelemetry("caligo-analytics", tracing => tracing.AddRedisInstrumentation());

builder.Services.AddHealthChecks()
    .AddCheck(
        "mongo",
        new MongoHealthCheck(
            builder.Configuration.GetConnectionString("Mongo"),
            builder.Configuration.GetValue("Analytics:MongoDatabase", "caligo_analytics")!),
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddRedis(
        _ => builder.Configuration.GetConnectionString("Redis") ?? string.Empty,
        name: "redis",
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "kafka",
        new KafkaBrokerHealthCheck(builder.Configuration["Kafka:BootstrapServers"] ?? string.Empty),
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "clickhouse",
        new ClickHouseHealthCheck(builder.Configuration.GetConnectionString("ClickHouse")),
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

builder.Services.Configure<ClickHouseOptions>(options =>
    options.ConnectionString = builder.Configuration.GetConnectionString("ClickHouse") ?? string.Empty);

builder.Services.AddSingleton<IViewStore, MongoViewStore>();
builder.Services.AddSingleton<ITrendingStore, RedisTrendingStore>();

builder.Services.AddSingleton<ClickHouseOlapStore>();
builder.Services.AddSingleton<IOlapSink>(provider => provider.GetRequiredService<ClickHouseOlapStore>());
builder.Services.AddSingleton<IOlapQueries>(provider => provider.GetRequiredService<ClickHouseOlapStore>());

builder.Services.AddOptions<RadioOptions>().BindConfiguration(RadioOptions.SectionName);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRadioStore, ClickHouseRadioStore>();
builder.Services.AddSingleton<IPlaybackStore, ClickHousePlaybackStore>();
builder.Services.AddSingleton<ILabStore, ClickHouseLabStore>();

builder.Services.AddHostedService<ClickHouseSchemaInitializer>();

builder.Services.AddHostedService<RawViewConsumer>();
builder.Services.AddHostedService<TrendingConsumer>();
builder.Services.AddHostedService<OlapViewConsumer>();
builder.Services.AddHostedService<OlapClapConsumer>();
builder.Services.AddHostedService<RadioCollector>();
builder.Services.AddHostedService<PlaybackConsumer>();

var app = builder.Build();

app.MapGrpcService<AnalyticsReaderService>();

app.MapCaligoHealthChecks();

app.MapGet("/", () => Results.Text(
    "Caligo.Analytics speaks gRPC.",
    "text/plain"));

app.Logger.LogInformation(
    "Analytics service starting. Mongo configured: {Mongo}. Redis configured: {Redis}. ClickHouse configured: {ClickHouse}.",
    app.Services.GetRequiredService<IViewStore>().IsConfigured,
    app.Services.GetRequiredService<ITrendingStore>().IsConfigured,
    app.Services.GetRequiredService<IOlapQueries>().IsConfigured);

app.Run();

namespace Caligo.Analytics.Host
{
    public sealed class AnalyticsHostMarker;
}
