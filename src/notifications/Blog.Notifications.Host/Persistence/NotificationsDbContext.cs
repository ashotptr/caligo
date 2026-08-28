using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Persistence;

/// <summary>
/// This service's own database.
///
/// Separate from the posts database on purpose: a subscriber list is this service's private
/// business, and letting another service read or write it would recreate the coupling the
/// split was meant to remove. It is a separate database on the same PostgreSQL instance rather
/// than a separate cluster - logical isolation is the part that matters architecturally, and
/// running one instance per service would be operational cost with no design benefit at this
/// size. Worth saying plainly rather than pretending otherwise.
/// </summary>
public class NotificationsDbContext : DbContext
{
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Subscriber> Subscribers => Set<Subscriber>();

    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var subscriber = builder.Entity<Subscriber>();

        subscriber.ToTable("subscribers");
        subscriber.HasKey(s => s.Id);
        subscriber.Property(s => s.Email).HasMaxLength(320).IsRequired();
        subscriber.Property(s => s.DisplayName).HasMaxLength(200);
        subscriber.Property(s => s.UnsubscribeToken).HasMaxLength(64).IsRequired();

        // One subscription per address, so a double sign-up cannot produce two emails.
        subscriber.HasIndex(s => s.Email).IsUnique();
        subscriber.HasIndex(s => s.UnsubscribeToken).IsUnique();
        subscriber.HasIndex(s => s.IsActive);

        var delivery = builder.Entity<NotificationDelivery>();

        delivery.ToTable("notification_deliveries");
        delivery.HasKey(d => d.Id);
        delivery.Property(d => d.PostTitle).HasMaxLength(200).IsRequired();
        delivery.Property(d => d.LastError).HasMaxLength(2000);
        delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);

        // The deduplication guarantee itself. Enforcing it in application code would race
        // between two relay instances; a unique index cannot.
        delivery.HasIndex(d => new { d.EventId, d.SubscriberId }).IsUnique();

        // Supports the sweeper's query for rows that never made it to the broker.
        delivery.HasIndex(d => new { d.Status, d.CreatedAt });
    }
}

/// <summary>
/// Used only by "dotnet ef" at design time, so scaffolding a migration does not boot the whole
/// service and start consuming from Kafka.
/// </summary>
public class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BLOG_NOTIFICATIONS_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=blognotifications;Username=postgres;Password=postgres";

        return new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(connectionString).Options);
    }
}
