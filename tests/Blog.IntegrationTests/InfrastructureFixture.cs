using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Kafka;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Blog.Posts.Infrastructure.Persistence;
using Xunit;

namespace Blog.IntegrationTests;

/// <summary>
/// Real PostgreSQL, Kafka and MongoDB in throwaway containers, started once for the whole
/// collection because starting a Kafka broker per test would dominate the run time.
/// </summary>
public class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("blogdb")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.7.1")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder()
        .WithImage("mongo:7")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public string MongoConnectionString => _mongo.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync()
    {
        // In parallel: three sequential container starts is most of the wall clock here.
        await Task.WhenAll(
            _postgres.StartAsync(), _kafka.StartAsync(), _mongo.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _kafka.DisposeAsync().AsTask(),
            _mongo.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "infrastructure";
}

/// <summary>
/// The real application, wired to the containers. Migrations run on start, exactly as they do
/// in production, so the schema under test is the one the migrations actually produce.
/// </summary>
public class ContainerApiFactory : WebApplicationFactory<Program>
{
    private readonly InfrastructureFixture _infrastructure;
    private readonly Dictionary<string, string?> _overrides;

    public ContainerApiFactory(InfrastructureFixture infrastructure, Dictionary<string, string?>? overrides = null)
    {
        _infrastructure = infrastructure;
        _overrides = overrides ?? new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "integration-test-signing-key-not-a-secret-0123456789",
                ["Jwt:Issuer"] = "blog-api",
                ["Jwt:Audience"] = "blog-client",

                ["Database:Provider"] = "Postgres",
                ["Database:FallbackToSqlite"] = "false",
                ["ConnectionStrings:DefaultConnection"] = _infrastructure.PostgresConnectionString,
                ["ConnectionStrings:Mongo"] = _infrastructure.MongoConnectionString,

                // No Redis: the distributed-memory cache stands in, which is enough for tests
                // that are about the outbox rather than about caching.
                ["ConnectionStrings:Redis"] = "",

                ["Kafka:BootstrapServers"] = _infrastructure.KafkaBootstrapServers,
                ["Kafka:OutboxPollInterval"] = "00:00:00.250",

                ["AdminUser:Email"] = "admin@test.local",
                ["AdminUser:Password"] = "AdminTest04!"
            };

            foreach (var (key, value) in _overrides)
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });
    }

    public ApplicationDbContext CreateContext()
    {
        var scope = Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public IServiceScope CreateScope() => Services.CreateScope();
}
