using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Services;

public interface ITokenService
{
    string CreateAccessToken(ApplicationUser user, IList<string> roles);

    /// <summary>
    /// Reads the subject of an expired-but-otherwise-valid access token, so a client can refresh
    /// without the server having to trust an unvalidated user id from the request body.
    /// Returns null when the token is not genuinely ours.
    /// </summary>
    string? GetUserIdFromExpiredToken(string accessToken);
}
