using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blog.Observability;

public class KafkaBrokerHealthCheck : IHealthCheck, IDisposable
{
    private readonly string _bootstrapServers;
    private readonly Lazy<IAdminClient>? _admin;

    public KafkaBrokerHealthCheck(string bootstrapServers)
    {
        _bootstrapServers = bootstrapServers;

        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _admin = new Lazy<IAdminClient>(() =>
                new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers })
                    .SetErrorHandler((_, _) => { })
                    .Build());
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_admin is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Kafka is not configured."));
        }

        try
        {
            var metadata = _admin.Value.GetMetadata(TimeSpan.FromSeconds(5));

            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s) reachable.")
                : HealthCheckResult.Unhealthy("Cluster reachable but reported no brokers."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Could not reach Kafka at {_bootstrapServers}.", ex));
        }
    }

    public void Dispose()
    {
        if (_admin is { IsValueCreated: true })
        {
            _admin.Value.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
