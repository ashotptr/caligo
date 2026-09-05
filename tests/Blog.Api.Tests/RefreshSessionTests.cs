using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;
using Blog.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blog.Api.Tests;

public class RefreshSessionTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;

    public RefreshSessionTests(SqliteApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task IssuedToken_IsNeverStoredInPlaintext()
    {
        using var scope = _factory.Services.CreateScope();

        var user = await CreateUserAsync(scope.ServiceProvider);
        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var token = await sessions.IssueAsync(user, "127.0.0.1", "xunit");

        await using var context = _factory.CreateContext();

        var stored = await context.UserSessions.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .ToListAsync();

        var session = Assert.Single(stored);

        Assert.NotEqual(token, session.TokenHash);
        Assert.Equal(RefreshSessionService.Hash(token), session.TokenHash);

        Assert.DoesNotContain(token, session.TokenHash);
    }

    [Fact]
    public async Task Rotate_IssuesANewToken_AndRetiresTheOldOne()
    {
        using var scope = _factory.Services.CreateScope();

        var user = await CreateUserAsync(scope.ServiceProvider);
        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var first = await sessions.IssueAsync(user, null, null);

        var result = await sessions.RotateAsync(first, null, null);

        Assert.Equal(RefreshOutcome.Success, result.Outcome);
        Assert.NotNull(result.RefreshToken);
        Assert.NotEqual(first, result.RefreshToken);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task ReplayingARotatedToken_IsDetected_AndRevokesTheChain()
    {
        using var scope = _factory.Services.CreateScope();

        var user = await CreateUserAsync(scope.ServiceProvider);
        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var stolen = await sessions.IssueAsync(user, null, null);

        var legitimate = await sessions.RotateAsync(stolen, null, null);
        Assert.Equal(RefreshOutcome.Success, legitimate.Outcome);

        var replay = await sessions.RotateAsync(stolen, null, null);

        Assert.Equal(RefreshOutcome.Reused, replay.Outcome);

        var afterBreach = await sessions.RotateAsync(legitimate.RefreshToken!, null, null);

        Assert.Equal(RefreshOutcome.Expired, afterBreach.Outcome);
    }

    [Fact]
    public async Task SeveralDevices_CanHoldLiveSessions_Simultaneously()
    {
        using var scope = _factory.Services.CreateScope();

        var user = await CreateUserAsync(scope.ServiceProvider);
        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var laptop = await sessions.IssueAsync(user, "10.0.0.1", "laptop");
        var phone = await sessions.IssueAsync(user, "10.0.0.2", "phone");

        Assert.Equal(RefreshOutcome.Success, (await sessions.RotateAsync(laptop, null, null)).Outcome);
        Assert.Equal(RefreshOutcome.Success, (await sessions.RotateAsync(phone, null, null)).Outcome);
    }

    [Fact]
    public async Task AnUnknownToken_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();

        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var result = await sessions.RotateAsync("not-a-token-anyone-ever-issued", null, null);

        Assert.Equal(RefreshOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task RevokeAll_EndsEverySessionForThatUser()
    {
        using var scope = _factory.Services.CreateScope();

        var user = await CreateUserAsync(scope.ServiceProvider);
        var sessions = scope.ServiceProvider.GetRequiredService<IRefreshSessionService>();

        var first = await sessions.IssueAsync(user, null, null);
        var second = await sessions.IssueAsync(user, null, null);

        await sessions.RevokeAllAsync(user.Id);

        Assert.Equal(RefreshOutcome.Expired, (await sessions.RotateAsync(first, null, null)).Outcome);
        Assert.Equal(RefreshOutcome.Expired, (await sessions.RotateAsync(second, null, null)).Outcome);
    }

    private static async Task<ApplicationUser> CreateUserAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test"
        };

        var result = await userManager.CreateAsync(user, "Str0ng!Passw0rd");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }
}
