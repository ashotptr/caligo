using System.ComponentModel.DataAnnotations.Schema;

namespace Blog.Posts.Infrastructure.Identity
{
    /// <summary>
    /// One refresh-token session. Replaces the single plaintext token that used to live on
    /// <see cref="ApplicationUser"/>, which meant a database leak handed the attacker usable
    /// tokens and a user could only be signed in on one device at a time.
    ///
    /// Only the SHA-256 hash is stored: refresh tokens are 256 bits of CSPRNG output, so unlike
    /// passwords they are not brute-forceable and need no slow KDF.
    /// </summary>
    public class UserSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string UserId { get; set; } = string.Empty;

        public ApplicationUser? User { get; set; }

        /// <summary>Hex-encoded SHA-256 of the refresh token. Never the token itself.</summary>
        public string TokenHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        /// <summary>
        /// Set when this session is rotated out. Presenting a token that has already been
        /// replaced means it leaked and is being replayed, which revokes the whole chain.
        /// </summary>
        public Guid? ReplacedBySessionId { get; set; }

        public string? CreatedByIp { get; set; }

        public string? UserAgent { get; set; }

        [NotMapped]
        public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
    }
}
