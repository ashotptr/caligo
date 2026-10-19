using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.NameTranslation;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Application.Insights;
using Caligo.Catalog.Application.Media;
using Caligo.Catalog.Application.Music;
using Caligo.Catalog.Domain.Users;
using Caligo.Catalog.Infrastructure.Media;
using Caligo.Catalog.Infrastructure.Music;
using Caligo.Catalog.Infrastructure.Persistence;
using Caligo.Catalog.Infrastructure.Schema;

namespace Caligo.Catalog.Infrastructure
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
