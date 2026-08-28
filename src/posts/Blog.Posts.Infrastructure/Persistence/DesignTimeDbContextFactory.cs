using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Blog.Posts.Infrastructure.Persistence
{
    /// <summary>
    /// Used only by "dotnet ef" at design time. Without it EF would boot the real application
    /// to find a DbContext, which would run the JWT key validation and the startup migration
    /// path just to scaffold a migration.
    ///
    /// Migrations are always scaffolded for PostgreSQL: SQLite is a development and test
    /// provider only, and creates its schema with EnsureCreated rather than migrations.
    /// </summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        private const string DefaultConnection =
            "Host=localhost;Port=5432;Database=blogdb;Username=postgres;Password=postgres";

        public ApplicationDbContext CreateDbContext(string[] args)
        {
            var connectionString =
                Environment.GetEnvironmentVariable("BLOG_MIGRATIONS_CONNECTION") ?? DefaultConnection;

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            return new ApplicationDbContext(options);
        }
    }
}
