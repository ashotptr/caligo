using Microsoft.AspNetCore.Identity;
using Blog.Catalog.Application.Abstractions;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Composition;

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
