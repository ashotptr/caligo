using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Blog.Api.Services;
using Blog.Posts.Infrastructure.Identity;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

[Collection(InfrastructureCollection.Name)]
public class RefreshRotationRaceTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private ContainerApiFactory _factory = null!;

    public RefreshRotationRaceTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public Task InitializeAsync()
    {
        _factory = new ContainerApiFactory(_infrastructure);
        _factory.CreateClient();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TenConcurrentRefreshesWithOneToken_HaveExactlyOneWinner()
    {
        string token;

        using (var scope = _factory.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var user = new ApplicationUser
            {
                UserName = $"racer-{Guid.NewGuid():N}",
                Email = $"{Guid.NewGuid():N}@example.test"
            };

            Assert.True((await users.CreateAsync(user, "Str0ng!Passw0rd")).Succeeded);

            token = await scope.ServiceProvider.GetRequiredService<IRefreshSessionService>()
                .IssueAsync(user, null, null);
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            using var scope = _factory.CreateScope();

            return (await scope.ServiceProvider.GetRequiredService<IRefreshSessionService>()
                .RotateAsync(token, null, null)).Outcome;
        })));

        Assert.Equal(1, outcomes.Count(o => o == RefreshOutcome.Success));
        Assert.All(outcomes.Where(o => o != RefreshOutcome.Success), o => Assert.Equal(RefreshOutcome.Reused, o));
    }
}
