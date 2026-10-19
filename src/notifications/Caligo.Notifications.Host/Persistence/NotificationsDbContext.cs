using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Caligo.Notifications.Core;

namespace Caligo.Notifications.Host.Persistence;

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

        subscriber.HasIndex(s => s.Email).IsUnique();
        subscriber.HasIndex(s => s.UnsubscribeToken).IsUnique();

        subscriber.HasIndex(s => s.Id)
            .HasDatabaseName("IX_subscribers_active_id")
            .HasFilter("\"IsActive\"");

        var delivery = builder.Entity<NotificationDelivery>();

        delivery.ToTable("notification_deliveries");
        delivery.HasKey(d => d.Id);
        delivery.Property(d => d.PostTitle).HasMaxLength(200).IsRequired();
        delivery.Property(d => d.LastError).HasMaxLength(2000);
        delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);

        delivery.HasIndex(d => new { d.EventId, d.SubscriberId }).IsUnique();

        delivery.HasIndex(d => new { d.Status, d.CreatedAt });
    }
}

public class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CALIGO_NOTIFICATIONS_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=caligonotifications;Username=postgres;Password=postgres";

        return new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(connectionString).Options);
    }
}
