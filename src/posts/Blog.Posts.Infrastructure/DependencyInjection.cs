using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Application.Claps;
using Blog.Posts.Application.Posts;
using Blog.Contracts.Analytics;
using Blog.Posts.Infrastructure.Analytics;
using Blog.Posts.Infrastructure.Caching;
using Blog.Posts.Infrastructure.Claps;
using Blog.Posts.Infrastructure.Messaging;
using Blog.Posts.Infrastructure.Persistence;
using Blog.Posts.Infrastructure.Publishing;
using Blog.Posts.Infrastructure.Search;

namespace Blog.Posts.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddPostsInfrastructure(this IServiceCollection services, bool isDevelopment)
        {
            // The provider is chosen once per process. The options callback below runs for every
            // scope, so resolving the choice there probed Postgres on every request, and a probe that
            // timed out under load quietly put that one request on the SQLite fallback.
            services.AddSingleton(provider => ResolveDatabase(
                provider.GetRequiredService<IConfiguration>(), isDevelopment));

            services.AddDbContext<ApplicationDbContext>((provider, options) =>
            {
                var setup = provider.GetRequiredService<DatabaseChoice>();

                options.UseProvider(setup.Provider, setup.ConnectionString);
            });

            services.AddOptions<KafkaOptions>()
                    .BindConfiguration(KafkaOptions.SectionName);

            services.AddOptions<AnalyticsClientOptions>()
                    .BindConfiguration(AnalyticsClientOptions.SectionName);

            services.AddOptions<PostingRules>()
                    .BindConfiguration(PostingRules.SectionName);

            services.AddOptions<ScheduledPublishingOptions>()
                    .BindConfiguration(ScheduledPublishingOptions.SectionName);

            services.AddOptions<ClapOptions>()
                    .BindConfiguration(ClapOptions.SectionName);

            services.AddSingleton(provider => provider.GetRequiredService<IOptions<PostingRules>>().Value);

            AddCaching(services);
            AddMessaging(services);
            AddAnalyticsClient(services);

            services.AddScoped<IPostRepository, EfPostRepository>();

            services.AddScoped<IPostSearch>(provider =>
            {
                var context = provider.GetRequiredService<ApplicationDbContext>();

                return context.Database.IsNpgsql()
                    ? new PostgresPostSearch(context)
                    : new SqlitePostSearch(context);
            });

            services.AddScoped<IClapStore>(provider =>
            {
                var context = provider.GetRequiredService<ApplicationDbContext>();
                var time = provider.GetRequiredService<TimeProvider>();

                return context.Database.IsNpgsql()
                    ? new PostgresClapStore(context, time)
                    : new EfClapStore(context, time);
            });

            services.AddSingleton(TimeProvider.System);
            services.AddScoped<PostService>();
            services.AddScoped<ClapService>();

            // Registered as singletons too, so tests and load tests can run a pass on demand.
            services.AddSingleton<ScheduledPostPublisher>();
            services.AddHostedService(provider => provider.GetRequiredService<ScheduledPostPublisher>());

            services.AddSingleton<ClapRollupWorker>();
            services.AddHostedService(provider => provider.GetRequiredService<ClapRollupWorker>());

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
            // One Redis connection shared by the cache entries and the generation counters. It never
            // aborts on a failed connect, so a Redis outage degrades to uncached reads, not a crash.
            services.AddSingleton(provider =>
            {
                var redis = provider.GetRequiredService<IConfiguration>().GetConnectionString("Redis");

                if (string.IsNullOrWhiteSpace(redis))
                {
                    return new RedisConnection(null);
                }

                var options = ConfigurationOptions.Parse(redis);
                options.AbortOnConnectFail = false;

                return new RedisConnection(ConnectionMultiplexer.Connect(options));
            });

            services.AddSingleton<IDistributedCache>(provider =>
            {
                if (provider.GetRequiredService<RedisConnection>().Multiplexer is { } multiplexer)
                {
                    return new RedisCache(new RedisCacheOptions
                    {
                        ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer),
                        InstanceName = "Blog_"
                    });
                }

                return new MemoryDistributedCache(
                    Options.Create(new MemoryDistributedCacheOptions()));
            });

            services.AddSingleton<IGenerationCounters>(provider =>
                provider.GetRequiredService<RedisConnection>().Multiplexer is { } multiplexer
                    ? new RedisGenerationCounters(multiplexer)
                    : new LocalGenerationCounters());

            services.AddSingleton<SingleFlight>();

            services.AddScoped<IPostCache, DistributedPostCache>();
        }

        private static void AddMessaging(IServiceCollection services)
        {
            services.AddSingleton<IKafkaProducer, KafkaProducer>();

            services.AddSingleton<IAnalyticsPublisher>(provider =>
                provider.GetRequiredService<IOptions<KafkaOptions>>().Value.Enabled
                    ? ActivatorUtilities.CreateInstance<KafkaAnalyticsPublisher>(provider)
                    : new NullAnalyticsPublisher());

            services.AddScoped<IDomainEventDispatcher>(provider =>
                provider.GetRequiredService<IOptions<KafkaOptions>>().Value.Enabled
                    ? ActivatorUtilities.CreateInstance<OutboxDomainEventDispatcher>(provider)
                    : ActivatorUtilities.CreateInstance<LoggingDomainEventDispatcher>(provider));

            services.AddHostedService<KafkaTopicProvisioner>();
            services.AddHostedService<OutboxRelay>();
        }

        private static void AddAnalyticsClient(IServiceCollection services)
        {
            services.AddGrpcClient<AnalyticsReader.AnalyticsReaderClient>((provider, grpc) =>
            {
                var options = provider.GetRequiredService<IOptions<AnalyticsClientOptions>>().Value;

                grpc.Address = new Uri(options.Enabled ? options.Address : "http://localhost:1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            });

            services.AddScoped<IViewStatsClient, GrpcViewStatsClient>();
        }
    }
}
