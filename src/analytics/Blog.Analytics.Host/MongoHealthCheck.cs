using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blog.Analytics.Host;

/// <summary>
/// Readiness check for MongoDB: does the server answer a ping.
///
/// Hand-written, like the Kafka and RabbitMQ checks, and for a concrete reason here:
/// AspNetCore.HealthChecks.MongoDb 8.x is built against driver 2.x, which had a separate
/// MongoDB.Driver.Core assembly. Driver 3.x merged it in, so the packaged check fails at
/// runtime with a missing-assembly error - a health check that reports the database down
/// because of its own dependency, which is worse than no check at all.
/// </summary>
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
            // Not configured is not unhealthy: this service runs without Mongo, it just cannot
            // answer historical questions.
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
