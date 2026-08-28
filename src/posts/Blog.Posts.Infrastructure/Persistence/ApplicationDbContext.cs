using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Blog.Posts.Domain.Events;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Posts.Infrastructure.Persistence
{
    /// <summary>
    /// One context for the whole service. Posts and ASP.NET Identity share a database because
    /// they are owned by the same service and posts carry a foreign key to their author; they
    /// are configured separately in <c>Configurations/</c> rather than in one long method.
    /// </summary>
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        /// <summary>
        /// Name of the generated tsvector shadow property. It is a shadow property so that
        /// <see cref="BlogPost"/> stays free of Npgsql types and remains a plain domain object.
        /// </summary>
        public const string SearchVectorProperty = "SearchVector";

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<BlogPost> BlogPosts => Set<BlogPost>();

        public DbSet<UserSession> UserSessions => Set<UserSession>();

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            var isNpgsql = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

            builder.ApplyConfiguration(new BlogPostConfiguration(Database.ProviderName));
            builder.ApplyConfiguration(new UserSessionConfiguration());
            builder.ApplyConfiguration(new OutboxMessageConfiguration(isNpgsql));
        }

        /// <summary>
        /// Posts carrying unpublished events, captured <em>before</em> saving. It has to happen
        /// before: a deleted entity leaves the change tracker once the save completes, so its
        /// event would otherwise be lost. The events themselves are built afterwards, by which
        /// point database-assigned identifiers are populated.
        /// </summary>
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
