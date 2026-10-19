using Microsoft.AspNetCore.Identity;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Api.Composition;

public class IdentityBlogAccounts : IBlogAccounts
{
    private readonly UserManager<ApplicationUser> _users;

    public IdentityBlogAccounts(UserManager<ApplicationUser> users)
    {
        _users = users;
    }

    public async Task<string?> GetEmailAsync(string identityId, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(identityId);

        return user?.Email;
    }
}
