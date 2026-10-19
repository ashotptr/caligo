using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Api.Services
{
    public enum RefreshOutcome
    {
        Success,
        Unknown,
        Expired,
        Reused
    }

    public readonly record struct RefreshResult(RefreshOutcome Outcome, ApplicationUser? User, string? RefreshToken)
    {
        public static RefreshResult Failed(RefreshOutcome outcome) => new(outcome, null, null);
    }

    public interface IRefreshSessionService
    {
        Task<string> IssueAsync(ApplicationUser user, string? ip, string? userAgent, CancellationToken cancellationToken = default);

        Task<RefreshResult> RotateAsync(string refreshToken, string? ip, string? userAgent, CancellationToken cancellationToken = default);

        Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
    }
}
