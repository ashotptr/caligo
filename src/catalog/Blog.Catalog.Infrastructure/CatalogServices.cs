using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.NameTranslation;
using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Activity;
using Blog.Catalog.Application.Contents;
using Blog.Catalog.Application.Insights;
using Blog.Catalog.Application.Media;
using Blog.Catalog.Application.Music;
using Blog.Catalog.Domain.Users;
using Blog.Catalog.Infrastructure.Media;
using Blog.Catalog.Infrastructure.Music;
using Blog.Catalog.Infrastructure.Persistence;
using Blog.Catalog.Infrastructure.Schema;

namespace Blog.Catalog.Infrastructure
{
    public static class CatalogServices
    {
        private static readonly INpgsqlNameTranslator EnumLabels = new NpgsqlNullNameTranslator();

        public static IServiceCollection AddCatalogInfrastructure(
            this IServiceCollection services, Action<IServiceProvider, CatalogOptions> configure)
        {
            services.AddOptions<CatalogOptions>()
                    .BindConfiguration(CatalogOptions.SectionName)
                    .Configure<IServiceProvider>((options, provider) => configure(provider, options));

            services.AddSingleton<StreamingSchema>();

            services.AddDbContext<StreamingDbContext>((provider, options) =>
            {
                var catalog = provider.GetRequiredService<IOptions<CatalogOptions>>().Value;

                if (!catalog.Enabled)
                {
                    return;
                }

                options.UseNpgsql(
                    WithSearchPath(catalog.ConnectionString),
                    npgsql => npgsql.MapEnum<Gender>("gender", StreamingSchema.Name, EnumLabels));
            });

            services.AddScoped<ICatalogReader, CatalogReader>();
            services.AddScoped<IStreamingAccounts, StreamingAccounts>();
            services.AddScoped<IActivityStore, ActivityStore>();
            services.AddScoped<IInsights, InsightsReader>();
            services.AddScoped<IMusicImportStore, MusicImportStore>();
            services.AddSingleton<IMusicLibrary, CaligoMusicLibrary>();

            services.AddOptions<MediaRules>().BindConfiguration(MediaRules.SectionName);
            services.AddSingleton(provider => provider.GetRequiredService<IOptions<MediaRules>>().Value);
            services.AddOptions<MediaStorageOptions>().BindConfiguration(MediaStorageOptions.SectionName);

            services.AddScoped<IMediaStore, MediaStore>();
            services.AddScoped<IPlaybackStore, PlaybackStore>();
            services.AddSingleton<IMediaInbox, MediaInbox>();
            services.AddScoped<IPackagingQueue, PackagingQueue>();

            services.AddScoped<CatalogService>();
            services.AddScoped<StreamingAccountService>();
            services.AddScoped<ActivityService>();
            services.AddScoped<InsightsService>();
            services.AddScoped<MusicImportService>();
            services.AddScoped<MediaService>();
            services.AddScoped<PlaybackService>();
            services.AddScoped<PackagingService>();

            return services;
        }

        public static string WithSearchPath(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            if (string.IsNullOrWhiteSpace(builder.SearchPath))
            {
                builder.SearchPath = StreamingSchema.Name + ",public";
            }

            return builder.ConnectionString;
        }
    }
}
