using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Blog.Notifications.Host;

/// <summary>
/// Readiness check for RabbitMQ: can this process open a connection and a channel.
///
/// Hand-written for the same reason as the Kafka one - the packaged check's options-based API
/// silently accepted a lambda that set nothing and then threw at startup, and a connection
/// attempt is a short, obvious way to answer the question.
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public RabbitMqHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ is not configured."));
        }

        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
            };

            using var connection = factory.CreateConnection("blog-notifications-healthcheck");
            using var channel = connection.CreateModel();

            return Task.FromResult(channel.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Connected, but the channel did not open."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Could not reach RabbitMQ.", ex));
        }
    }
}
