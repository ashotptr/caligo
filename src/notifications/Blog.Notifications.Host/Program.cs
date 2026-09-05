using System.ComponentModel.DataAnnotations;
using Blog.Observability;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using Blog.Notifications.Core;
using Blog.Notifications.Host;
using Blog.Notifications.Host.Email;
using Blog.Notifications.Host.Messaging;
using Blog.Notifications.Host.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddBlogTelemetry(
    "blog-notifications",
    tracing => tracing.AddEntityFrameworkCoreInstrumentation());

builder.Services.AddHealthChecks()
    .AddNpgSql(
        _ => builder.Configuration.GetConnectionString("Notifications") ?? string.Empty,
        name: "postgres",
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "rabbitmq",
        new RabbitMqHealthCheck(builder.Configuration["Notifications:RabbitMq"] ?? string.Empty),
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "kafka",
        new KafkaBrokerHealthCheck(
            builder.Configuration["Notifications:KafkaBootstrapServers"] ?? string.Empty),
        tags: new[] { HealthEndpoints.ReadinessTag });

builder.Services.AddOptions<NotificationsOptions>().BindConfiguration(NotificationsOptions.SectionName);
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.SectionName);

builder.Services.AddDbContext<NotificationsDbContext>((provider, options) =>
{
    var connectionString = provider.GetRequiredService<IConfiguration>()
        .GetConnectionString("Notifications")
        ?? throw new InvalidOperationException("ConnectionStrings:Notifications is required.");

    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<ISubscriberStore, EfSubscriberStore>();
builder.Services.AddScoped<IDeliveryLedger, EfDeliveryLedger>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

builder.Services.AddSingleton<RabbitCommandQueue>();
builder.Services.AddSingleton<ICommandQueue>(p => p.GetRequiredService<RabbitCommandQueue>());

builder.Services.AddHostedService<PostLifecycleRelay>();
builder.Services.AddHostedService<EmailWorker>();
builder.Services.AddHostedService<StrandedDeliverySweeper>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            context.Database.Migrate();

            break;
        }
        catch (Exception ex) when (attempt < 5)
        {
            app.Logger.LogWarning(ex, "Database not ready (attempt {Attempt}/5), retrying in 3 seconds...", attempt);

            await Task.Delay(3000);
        }
    }
}

app.MapBlogHealthChecks();

app.MapPost("/api/subscriptions", async (
    SubscribeRequest request, ISubscriberStore store, CancellationToken cancellationToken) =>
{
    var validation = new List<ValidationResult>();

    if (!Validator.TryValidateObject(request, new ValidationContext(request), validation, validateAllProperties: true))
    {
        return Results.BadRequest(validation.Select(v => v.ErrorMessage));
    }

    var subscriber = await store.SubscribeAsync(request.Email, request.DisplayName, cancellationToken);

    return Results.Ok(new { subscriber.Email, subscriber.IsActive });
});

app.MapGet("/api/subscriptions/unsubscribe/{token}", async (
    string token, ISubscriberStore store, CancellationToken cancellationToken) =>
{
    var removed = await store.UnsubscribeAsync(token, cancellationToken);

    return removed
        ? Results.Text("You have been unsubscribed.", "text/plain")
        : Results.NotFound("That unsubscribe link is not valid.");
});

app.MapGet("/api/subscriptions/health", (
    ICommandQueue queue, IEmailSender email) => Results.Ok(new
{
    rabbit = queue.IsConfigured,
    smtp = email.IsConfigured
}));

app.Run();

public record SubscribeRequest(
    [property: Required, EmailAddress, StringLength(320)] string Email,
    [property: StringLength(200)] string? DisplayName);

namespace Blog.Notifications.Host
{
    public sealed class NotificationsHostMarker;
}
