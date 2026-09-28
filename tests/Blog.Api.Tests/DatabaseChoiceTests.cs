using Blog.Posts.Infrastructure;
using Blog.Posts.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blog.Api.Tests;

public class DatabaseChoiceTests
{
    // Every DbContext must use the provider the process chose at startup. When each scope made its
    // own choice, a Postgres probe that timed out under load put that one request on SQLite.
    [Fact]
    public void EveryScope_UsesTheProviderChosenForTheProcess()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unused",
                ["Database:FallbackToSqlite"] = "false"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddPostsInfrastructure(isDevelopment: false);

        // What startup would have decided had the probe failed; the configuration alone says Postgres.
        services.AddSingleton(new DependencyInjection.DatabaseChoice(
            DatabaseProvider.Sqlite, "Data Source=:memory:", FellBackToSqlite: true));

        using var root = services.BuildServiceProvider();

        for (var request = 0; request < 3; request++)
        {
            using var scope = root.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            Assert.True(context.Database.IsSqlite());
        }
    }
}
