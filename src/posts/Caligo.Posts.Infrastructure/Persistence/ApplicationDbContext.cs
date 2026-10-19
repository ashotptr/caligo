using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Caligo.Posts.Domain.Events;
using Caligo.Posts.Domain.Posts;
using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Posts.Infrastructure.Persistence
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public const string SearchVectorProperty = "SearchVector";

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<BlogPost> BlogPosts => Set<BlogPost>();

        public DbSet<UserSession> UserSessions => Set<UserSession>();

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        public DbSet<PostClap> PostClaps => Set<PostClap>();

        public DbSet<ClapDelta> ClapDeltas => Set<ClapDelta>();

        public DbSet<PostClapTotal> PostClapTotals => Set<PostClapTotal>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            var isNpgsql = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

            builder.ApplyConfiguration(new BlogPostConfiguration(Database.ProviderName));
            builder.ApplyConfiguration(new UserSessionConfiguration());
            builder.ApplyConfiguration(new OutboxMessageConfiguration(isNpgsql));

            var claps = new ClapConfiguration(isNpgsql);

            builder.ApplyConfiguration<PostClap>(claps);
            builder.ApplyConfiguration<ClapDelta>(claps);
            builder.ApplyConfiguration<PostClapTotal>(claps);
        }

        public IReadOnlyList<BlogPost> CaptureEventSources() =>
            ChangeTracker.Entries<BlogPost>()
                .Select(entry => entry.Entity)
                .Where(post => post.HasPendingEvents)
                .ToList();

        public static IReadOnlyList<IDomainEvent> DrainEvents(IReadOnlyList<BlogPost> sources)
        {
            if (sources.Count == 0)
            {
                return Array.Empty<IDomainEvent>();
            }

            return sources.SelectMany(post => post.DrainDomainEvents()).ToList();
        }
    }
}
