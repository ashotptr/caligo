using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Services;

public interface ITokenService
{
    string CreateAccessToken(ApplicationUser user, IList<string> roles);

    string? GetUserIdFromExpiredToken(string accessToken);
}
