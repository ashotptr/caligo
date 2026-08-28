using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Blog.Posts.Infrastructure.Persistence
{
    public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        private readonly bool _isNpgsql;

        public OutboxMessageConfiguration(bool isNpgsql)
        {
            _isNpgsql = isNpgsql;
        }

        public void Configure(EntityTypeBuilder<OutboxMessage> outbox)
        {
            outbox.ToTable("outbox_messages");

            outbox.HasKey(m => m.Id);

            outbox.Property(m => m.EventType).HasMaxLength(128).IsRequired();
            outbox.Property(m => m.Topic).HasMaxLength(128).IsRequired();
            outbox.Property(m => m.PartitionKey).HasMaxLength(128).IsRequired();
            outbox.Property(m => m.Payload).IsRequired();
            outbox.Property(m => m.LastError).HasMaxLength(2000);
            outbox.Property(m => m.TraceParent).HasMaxLength(64);

            if (_isNpgsql)
            {
                // Partial index: the dispatcher only ever asks for unsent rows, so the index
                // stays the size of the backlog rather than the size of the whole table. Without
                // the filter it would grow forever alongside the history of every event sent.
                outbox.HasIndex(m => m.OccurredAt)
                      .HasDatabaseName("IX_outbox_messages_pending")
                      .HasFilter("\"ProcessedAt\" IS NULL");

                outbox.Property(m => m.Payload).HasColumnType("jsonb");
            }
            else
            {
                outbox.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
            }
        }
    }
}
