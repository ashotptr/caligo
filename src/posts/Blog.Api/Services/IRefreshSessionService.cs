using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Services
{
    public enum RefreshOutcome
    {
        Success,

        /// <summary>No session matches the presented token.</summary>
        Unknown,

        /// <summary>The session exists but has lapsed or was explicitly revoked.</summary>
        Expired,

        /// <summary>
        /// The token was already rotated out. Rotation means a valid client never presents the
        /// same refresh token twice, so this is evidence of theft.
        /// </summary>
        Reused
    }

    public readonly record struct RefreshResult(RefreshOutcome Outcome, ApplicationUser? User, string? RefreshToken)
    {
        public static RefreshResult Failed(RefreshOutcome outcome) => new(outcome, null, null);
    }

    public interface IRefreshSessionService
    {
        /// <summary>Creates a session and returns the plaintext refresh token, which is never stored.</summary>
        Task<string> IssueAsync(ApplicationUser user, string? ip, string? userAgent, CancellationToken cancellationToken = default);

        /// <summary>Validates and rotates a refresh token, revoking the chain if replay is detected.</summary>
        Task<RefreshResult> RotateAsync(string refreshToken, string? ip, string? userAgent, CancellationToken cancellationToken = default);

        /// <summary>Revokes every live session for a user, e.g. on sign-out-everywhere.</summary>
        Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
    }
}
