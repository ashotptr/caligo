using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Posts;
using Blog.Contracts.Analytics;
using Blog.Posts.Infrastructure.Analytics;
using Blog.Posts.Infrastructure.Caching;
using Blog.Posts.Infrastructure.Messaging;
using Blog.Posts.Infrastructure.Persistence;
using Blog.Posts.Infrastructure.Search;

namespace Blog.Posts.Infrastructure
{
    /// <summary>
    /// Composition root for everything that talks to the outside world. The API host says
    /// "add the infrastructure" and does not otherwise know which database, cache or broker is
    /// in play - that choice lives here, next to the code that implements it.
    ///
    /// Every configuration-dependent decision is made when the service is <em>resolved</em>,
    /// not while it is being registered. Reading configuration during registration looks
    /// harmless but sees only the sources bound at that instant: a host that layers its own
    /// configuration afterwards - a test host, or anything using IHostBuilder - would be
    /// silently ignored, and the application would come up against the wrong database with no
    /// error to explain why.
    /// </summary>
    public static class DependencyInjection
    {
        public static IServiceCollection AddPostsInfrastructure(this IServiceCollection services, bool isDevelopment)
        {
            services.AddDbContext<ApplicationDbContext>((provider, options) =>
            {
                var setup = ResolveDatabase(provider.GetRequiredService<IConfiguration>(), isDevelopment);

                options.UseProvider(setup.Provider, setup.ConnectionString);
            });

            services.AddSingleton(provider => ResolveDatabase(
                provider.GetRequiredService<IConfiguration>(), isDevelopment));

            services.AddOptions<KafkaOptions>()
                    .BindConfiguration(KafkaOptions.SectionName);

            services.AddOptions<AnalyticsClientOptions>()
                    .BindConfiguration(AnalyticsClientOptions.SectionName);

            AddCaching(services);
            AddMessaging(services);
            AddAnalyticsClient(services);

            services.AddScoped<IPostRepository, EfPostRepository>();

            // Search follows the provider, decided from the context that was actually built.
            services.AddScoped<IPostSearch>(provider =>
            {
                var context = provider.GetRequiredService<ApplicationDbContext>();

                return context.Database.IsNpgsql()
                    ? new PostgresPostSearch(context)
                    : new SqlitePostSearch(context);
            });

            services.AddSingleton(TimeProvider.System);
            services.AddScoped<PostService>();

            return services;
        }

        public record DatabaseChoice(DatabaseProvider Provider, string ConnectionString, bool FellBackToSqlite);

        private static DatabaseChoice ResolveDatabase(IConfiguration configuration, bool isDevelopment)
        {
            var provider = DatabaseSetup.ResolveProvider(configuration);
            var connectionString = DatabaseSetup.ResolveConnectionString(configuration, provider);

            if (provider == DatabaseProvider.Postgres
                && DatabaseSetup.ShouldFallBackToSqlite(configuration, isDevelopment)
                && !DatabaseSetup.CanReachPostgres(connectionString))
            {
                return new DatabaseChoice(
                    DatabaseProvider.Sqlite, $"Data Source={DatabaseSetup.DefaultSqliteFile}", true);
            }

            return new DatabaseChoice(provider, connectionString, false);
        }

        private static void AddCaching(IServiceCollection services)
        {
            // Redis when configured, an in-process cache otherwise, so the cache-aside path is
            // always exercised locally without requiring Redis to be running. Built by a factory
            // rather than by AddStackExchangeRedisCache / AddDistributedMemoryCache so that the
            // choice is made once configuration is complete.
            services.AddSingleton<IDistributedCache>(provider =>
            {
                var redis = provider.GetRequiredService<IConfiguration>().GetConnectionString("Redis");

                if (!string.IsNullOrWhiteSpace(redis))
                {
                    return new RedisCache(new RedisCacheOptions
                    {
                        Configuration = redis,
                        InstanceName = "Blog_"
                    });
                }

                return new MemoryDistributedCache(
                    Options.Create(new MemoryDistributedCacheOptions()));
            });

            services.AddScoped<IPostCache, DistributedPostCache>();
        }

        private static void AddMessaging(IServiceCollection services)
        {
            // The producer is a singleton because the Confluent client is thread-safe and owns
            // its own connection pool and batching; one per call would defeat both.
            //
            // Registered once, by interface. Adding the concrete type as well and resolving it
            // through a factory would make the container track the same instance twice and call
            // Dispose on it twice, which throws once the native handle is gone.
            services.AddSingleton<IKafkaProducer, KafkaProducer>();

            services.AddSingleton<IAnalyticsPublisher>(provider =>
                provider.GetRequiredService<IOptions<KafkaOptions>>().Value.Enabled
                    ? ActivatorUtilities.CreateInstance<KafkaAnalyticsPublisher>(provider)
                    : new NullAnalyticsPublisher());

            services.AddScoped<IDomainEventDispatcher>(provider =>
                provider.GetRequiredService<IOptions<KafkaOptions>>().Value.Enabled
                    ? ActivatorUtilities.CreateInstance<OutboxDomainEventDispatcher>(provider)
                    : ActivatorUtilities.CreateInstance<LoggingDomainEventDispatcher>(provider));

            // Registered unconditionally; each one stops immediately when Kafka is not
            // configured. A hosted service cannot be added later, so the decision has to be
            // inside it rather than around it.
            services.AddHostedService<KafkaTopicProvisioner>();
            services.AddHostedService<OutboxRelay>();
        }

        private static void AddAnalyticsClient(IServiceCollection services)
        {
            services.AddGrpcClient<AnalyticsReader.AnalyticsReaderClient>((provider, grpc) =>
            {
                var options = provider.GetRequiredService<IOptions<AnalyticsClientOptions>>().Value;

                // A placeholder keeps the factory happy when analytics is switched off; the
                // client short-circuits before any call is made.
                grpc.Address = new Uri(options.Enabled ? options.Address : "http://localhost:1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // gRPC runs on one long-lived HTTP/2 connection, so the periodic DNS refresh
                // that connection recycling normally provides has to be asked for explicitly -
                // otherwise the client keeps talking to a pod that has been replaced.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // Detects a peer that vanished without closing the connection, which is what
                // a killed container looks like from here.
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            });

            services.AddScoped<IViewStatsClient, GrpcViewStatsClient>();
        }
    }
}
