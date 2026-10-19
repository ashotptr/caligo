using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Caligo.Posts.Application.Abstractions;
using Caligo.Posts.Application.Claps;
using Caligo.Posts.Application.Posts;
using Caligo.Contracts.Analytics;
using Caligo.Posts.Infrastructure.Analytics;
using Caligo.Posts.Infrastructure.Caching;
using Caligo.Posts.Infrastructure.Claps;
using Caligo.Posts.Infrastructure.Messaging;
using Caligo.Posts.Infrastructure.Persistence;
using Caligo.Posts.Infrastructure.Publishing;
using Caligo.Posts.Infrastructure.Search;

namespace Caligo.Posts.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddPostsInfrastructure(this IServiceCollection services, bool isDevelopment)
        {
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
                        InstanceName = "Caligo_"
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
