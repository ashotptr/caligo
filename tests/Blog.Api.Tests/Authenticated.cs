using System.Net.Http.Headers;
using Blog.Api.Services;
using Blog.Posts.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blog.Api.Tests;

internal static class Authenticated
{
    public static async Task<(HttpClient Client, string UserId)> ClientAsync(
        WebApplicationFactory<Program> factory, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = $"user-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test"
        };

        var created = await users.CreateAsync(user, "Str0ng!Passw0rd");

        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, roles);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, user.Id);
    }
}
