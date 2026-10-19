using Caligo.Posts.Infrastructure;
using Caligo.Posts.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caligo.Api.Tests;

public class DatabaseChoiceTests
{
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
