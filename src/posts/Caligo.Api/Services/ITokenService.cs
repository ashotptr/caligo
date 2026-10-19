using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Api.Services;

public interface ITokenService
{
    string CreateAccessToken(ApplicationUser user, IList<string> roles);

    string? GetUserIdFromExpiredToken(string accessToken);
}
