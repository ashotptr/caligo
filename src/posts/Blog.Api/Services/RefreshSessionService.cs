using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Blog.Posts.Infrastructure.Persistence;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Services
{
    public class RefreshSessionService : IRefreshSessionService
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RefreshSessionService> _logger;

        public RefreshSessionService(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IConfiguration configuration,
            ILogger<RefreshSessionService> logger)
        {
            _context = context;
            _userManager = userManager;
            _configuration = configuration;
            _logger = logger;
        }

        private TimeSpan Lifetime =>
            TimeSpan.FromDays(_configuration.GetValue("Jwt:RefreshTokenDays", 7));

        public static string Hash(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        public async Task<string> IssueAsync(ApplicationUser user, string? ip, string? userAgent, CancellationToken cancellationToken = default)
        {
            var token = CreateToken();

            _context.UserSessions.Add(new UserSession
            {
                UserId = user.Id,
                TokenHash = Hash(token),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(Lifetime),
                CreatedByIp = Truncate(ip, 64),
                UserAgent = Truncate(userAgent, 256)
            });

            await _context.SaveChangesAsync(cancellationToken);

            return token;
        }

        public async Task<RefreshResult> RotateAsync(string refreshToken, string? ip, string? userAgent, CancellationToken cancellationToken = default)
        {
            var hash = Hash(refreshToken);

            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);

            if (session is null)
            {
                return RefreshResult.Failed(RefreshOutcome.Unknown);
            }

            if (session.ReplacedBySessionId is not null)
            {
                await RevokeAllAsync(session.UserId, cancellationToken);

                _logger.LogWarning(
                    "Refresh token replay detected for user {UserId}; all sessions revoked.", session.UserId);

                return RefreshResult.Failed(RefreshOutcome.Reused);
            }

            if (!session.IsActive)
            {
                return RefreshResult.Failed(RefreshOutcome.Expired);
            }

            var user = await _userManager.FindByIdAsync(session.UserId);

            if (user is null)
            {
                return RefreshResult.Failed(RefreshOutcome.Unknown);
            }

            var token = CreateToken();

            var replacement = new UserSession
            {
                UserId = session.UserId,
                TokenHash = Hash(token),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(Lifetime),
                CreatedByIp = Truncate(ip, 64),
                UserAgent = Truncate(userAgent, 256)
            };

            _context.UserSessions.Add(replacement);

            session.RevokedAt = DateTime.UtcNow;
            session.ReplacedBySessionId = replacement.Id;

            await _context.SaveChangesAsync(cancellationToken);

            return new RefreshResult(RefreshOutcome.Success, user, token);
        }

        public async Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;

            await _context.UserSessions
                .Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), cancellationToken);

            foreach (var entry in _context.ChangeTracker.Entries<UserSession>())
            {
                if (entry.Entity.UserId == userId && entry.Entity.RevokedAt is null)
                {
                    entry.Entity.RevokedAt = now;

                    entry.State = EntityState.Unchanged;
                }
            }
        }

        private static string CreateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);

            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string? Truncate(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
    }
}
