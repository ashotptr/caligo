using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Blog.Posts.Infrastructure.Persistence
{
    public enum DatabaseProvider
    {
        Postgres,
        Sqlite
    }

    public static class DatabaseSetup
    {
        public const string DefaultSqliteFile = "blog.db";

        public static DatabaseProvider ResolveProvider(IConfiguration configuration)
        {
            var configured = configuration.GetValue<string>("Database:Provider");

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim().ToLowerInvariant() switch
                {
                    "sqlite" => DatabaseProvider.Sqlite,
                    "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
                    _ => throw new InvalidOperationException(
                        $"Unknown Database:Provider '{configured}'. Use 'Postgres' or 'Sqlite'.")
                };
            }

            var connectionString = configuration.GetConnectionString("DefaultConnection");

            return string.IsNullOrWhiteSpace(connectionString)
                ? DatabaseProvider.Sqlite
                : DatabaseProvider.Postgres;
        }

        public static string ResolveConnectionString(
            IConfiguration configuration, DatabaseProvider provider)
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            if (provider == DatabaseProvider.Sqlite)
            {
                return $"Data Source={DefaultSqliteFile}";
            }

            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required when using Postgres.");
        }

        /// <summary>
        /// Whether an unreachable PostgreSQL should silently become SQLite. Defaults to on in
        /// development so the API runs with nothing else started, and off everywhere else,
        /// where quietly writing to a throwaway file would be far worse than failing to start.
        /// </summary>
        public static bool ShouldFallBackToSqlite(IConfiguration configuration, bool isDevelopment)
        {
            var configured = configuration.GetValue<bool?>("Database:FallbackToSqlite");

            return configured ?? isDevelopment;
        }

        public static bool CanReachPostgres(string connectionString, int timeoutSeconds = 3)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timeout = timeoutSeconds,
                    CommandTimeout = timeoutSeconds
                };

                using var connection = new NpgsqlConnection(builder.ConnectionString);

                connection.Open();

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static DbContextOptionsBuilder UseProvider(
            this DbContextOptionsBuilder options,
            DatabaseProvider provider,
            string connectionString)
        {
            return provider switch
            {
                DatabaseProvider.Sqlite => options.UseSqlite(connectionString),
                _ => options.UseNpgsql(connectionString)
            };
        }
    }
}
