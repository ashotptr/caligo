using ClickHouse.Client.ADO;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blog.Analytics.Host;

public class ClickHouseHealthCheck : IHealthCheck
{
    private readonly string? _connectionString;

    public ClickHouseHealthCheck(string? connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return HealthCheckResult.Healthy("ClickHouse is not configured.");
        }

        try
        {
            using var connection = new ClickHouseConnection(_connectionString);
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT 1";

            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not reach ClickHouse.", ex);
        }
    }
}
