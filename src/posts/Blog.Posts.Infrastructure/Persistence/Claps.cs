using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blog.Posts.Domain.Posts;

namespace Blog.Posts.Infrastructure.Persistence
{
    // One row per reader per post, capped at ClapRules.MaxPerReader.
    public class PostClap
    {
        public int PostId { get; set; }

        public string UserId { get; set; } = string.Empty;

        public int Claps { get; set; }

        public int LastApplied { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    // Append-only queue of accepted claps, folded into post_clap_totals in batches by ClapRollupWorker.
    public class ClapDelta
    {
        public long Id { get; set; }

        public int PostId { get; set; }

        public string UserId { get; set; } = string.Empty;

        public int Delta { get; set; }

        public bool FirstClap { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public class PostClapTotal
    {
        public int PostId { get; set; }

        public long Claps { get; set; }

        public long Clappers { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    public class ClapConfiguration :
        IEntityTypeConfiguration<PostClap>,
        IEntityTypeConfiguration<ClapDelta>,
        IEntityTypeConfiguration<PostClapTotal>
    {
        private readonly bool _isNpgsql;

        public ClapConfiguration(bool isNpgsql)
        {
            _isNpgsql = isNpgsql;
        }

        public void Configure(EntityTypeBuilder<PostClap> clap)
        {
            clap.ToTable("post_claps");

            clap.HasKey(c => new { c.PostId, c.UserId });

            clap.Property(c => c.UserId).HasMaxLength(450);

            clap.HasOne<BlogPost>()
                .WithMany()
                .HasForeignKey(c => c.PostId)
                .OnDelete(DeleteBehavior.Cascade);
        }

        public void Configure(EntityTypeBuilder<ClapDelta> delta)
        {
            delta.ToTable("clap_deltas");

            delta.HasKey(d => d.Id);

            delta.Property(d => d.UserId).HasMaxLength(450);

            if (_isNpgsql)
            {
                // A queue table churns: every row is inserted once and deleted once. Vacuum it
                // after a few thousand dead rows instead of after 20% of the table.
                delta.HasStorageParameter("autovacuum_vacuum_scale_factor", 0.0);
                delta.HasStorageParameter("autovacuum_vacuum_threshold", 5000);
            }
        }

        public void Configure(EntityTypeBuilder<PostClapTotal> total)
        {
            total.ToTable("post_clap_totals");

            total.HasKey(t => t.PostId);

            total.HasOne<BlogPost>()
                .WithMany()
                .HasForeignKey(t => t.PostId)
                .OnDelete(DeleteBehavior.Cascade);

            if (_isNpgsql)
            {
                // The counter lives in its own narrow table rather than on BlogPosts, and leaves free
                // space on every page so the update can be a HOT update (no index maintenance).
                total.HasStorageParameter("fillfactor", 70);
            }
        }
    }
}
