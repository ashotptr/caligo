using System.ComponentModel.DataAnnotations.Schema;

namespace Caligo.Posts.Infrastructure.Identity
{
    public class UserSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string UserId { get; set; } = string.Empty;

        public ApplicationUser? User { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        public Guid? ReplacedBySessionId { get; set; }

        public string? CreatedByIp { get; set; }

        public string? UserAgent { get; set; }

        [NotMapped]
        public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
    }
}
