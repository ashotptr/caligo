using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blog.Analytics.Host;

public class MongoHealthCheck : IHealthCheck
{
    private readonly IMongoClient? _client;
    private readonly string _database;

    public MongoHealthCheck(string? connectionString, string database)
    {
        _database = database;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);

        _client = new MongoClient(settings);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return HealthCheckResult.Healthy("MongoDB is not configured.");
        }

        try
        {
            await _client.GetDatabase(_database)
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not reach MongoDB.", ex);
        }
    }
}
