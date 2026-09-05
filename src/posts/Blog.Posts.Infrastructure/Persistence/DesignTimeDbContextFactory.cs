using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Blog.Posts.Infrastructure.Persistence
{
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
