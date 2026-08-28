using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blog.Observability;

/// <summary>
/// Readiness check for Kafka: can this process reach the cluster and read its metadata.
///
/// Written by hand rather than taken from AspNetCore.HealthChecks.Kafka, because that check
/// works by <em>producing a message</em> to a probe topic on every poll. Two problems with
/// that here: the brokers have auto-create disabled on purpose, so the probe topic does not
/// exist and the check fails for a reason unrelated to health; and a readiness probe that
/// writes to the system it is checking is a side effect nobody asked for, running every ten
/// seconds, in every replica, forever.
///
/// A metadata request answers the same question - is the cluster reachable and does it have
/// brokers - and changes nothing.
/// </summary>
public class KafkaBrokerHealthCheck : IHealthCheck, IDisposable
{
    private readonly string _bootstrapServers;
    private readonly Lazy<IAdminClient>? _admin;

    public KafkaBrokerHealthCheck(string bootstrapServers)
    {
        _bootstrapServers = bootstrapServers;

        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            // One admin client, built on first use: it holds connections, so creating one per
            // probe would churn sockets against the broker every few seconds.
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
            // Not configured is not unhealthy: the services are designed to run without a
            // broker, just without the features that need one.
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
