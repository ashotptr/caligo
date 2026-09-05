using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Posts.Infrastructure.Persistence
{
    public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
    {
        public void Configure(EntityTypeBuilder<UserSession> session)
        {
            session.HasKey(s => s.Id);

            session.HasIndex(s => s.TokenHash).IsUnique();
            session.HasIndex(s => new { s.UserId, s.ExpiresAt });

            session.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
            session.Property(s => s.UserId).IsRequired();
            session.Property(s => s.CreatedByIp).HasMaxLength(64);
            session.Property(s => s.UserAgent).HasMaxLength(256);

            session.HasOne(s => s.User)
                   .WithMany()
                   .HasForeignKey(s => s.UserId)
                   .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
