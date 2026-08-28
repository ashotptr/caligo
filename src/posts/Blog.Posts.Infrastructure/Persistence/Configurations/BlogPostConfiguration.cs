using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;
using System.Text.Json;
using Blog.Posts.Domain.Posts;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Posts.Infrastructure.Persistence
{
    /// <summary>
    /// All the storage decisions for <see cref="BlogPost"/>, kept out of the entity itself so
    /// the domain stays free of persistence concerns.
    /// </summary>
    public class BlogPostConfiguration : IEntityTypeConfiguration<BlogPost>
    {
        private readonly string? _providerName;

        public BlogPostConfiguration(string? providerName)
        {
            _providerName = providerName;
        }

        private bool IsNpgsql => _providerName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        private bool IsSqlite => _providerName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        public void Configure(EntityTypeBuilder<BlogPost> post)
        {
            post.HasKey(p => p.Id);

            post.Property(p => p.Title).IsRequired().HasMaxLength(200);
            post.Property(p => p.Content).IsRequired();
            post.Property(p => p.PublishedDate).IsRequired();

            // The domain type exposes IReadOnlyList; EF needs the backing field to write to.
            post.Property(p => p.Tags)
                .HasField("_tags")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            // Every list query orders by this pair, and it is also the keyset cursor.
            post.HasIndex(p => new { p.PublishedDate, p.Id });

            // The author is an Identity user. The relationship is declared here, without a
            // navigation property on the entity, so the domain never references Identity.
            post.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(p => p.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);

            if (IsNpgsql)
            {
                // Stored generated column: Postgres recomputes it on write, so reads are a plain
                // GIN index lookup. Title is weighted above Content so a title hit outranks a
                // passing mention in the body.
                post.Property<NpgsqlTsVector>(ApplicationDbContext.SearchVectorProperty)
                    .HasColumnName("search_vector")
                    .HasComputedColumnSql(
                        "setweight(to_tsvector('english', coalesce(\"Title\", '')), 'A') || " +
                        "setweight(to_tsvector('english', coalesce(\"Content\", '')), 'B')",
                        stored: true);

                post.HasIndex(ApplicationDbContext.SearchVectorProperty).HasMethod("GIN");

                // Tags is a text[]; a GIN index makes containment an index lookup too.
                post.HasIndex(p => p.Tags).HasMethod("GIN");
            }

            if (IsSqlite)
            {
                // SQLite has no array type, so tags round-trip as JSON. The comparer is required
                // or EF cannot detect in-place mutation of the list.
                var converter = new ValueConverter<IReadOnlyList<string>, string>(
                    tags => JsonSerializer.Serialize(tags, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null)
                            ?? new List<string>());

                var comparer = new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    tags => tags.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode())),
                    tags => tags.ToList());

                post.Property(p => p.Tags)
                    .HasConversion(converter, comparer)
                    .HasColumnType("TEXT");
            }
        }
    }
}
