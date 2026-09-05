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

            post.Property(p => p.Tags)
                .HasField("_tags")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            post.HasIndex(p => new { p.PublishedDate, p.Id });

            post.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(p => p.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);

            if (IsNpgsql)
            {
                post.Property<NpgsqlTsVector>(ApplicationDbContext.SearchVectorProperty)
                    .HasColumnName("search_vector")
                    .HasComputedColumnSql(
                        "setweight(to_tsvector('english', coalesce(\"Title\", '')), 'A') || " +
                        "setweight(to_tsvector('english', coalesce(\"Content\", '')), 'B')",
                        stored: true);

                post.HasIndex(ApplicationDbContext.SearchVectorProperty).HasMethod("GIN");

                post.HasIndex(p => p.Tags).HasMethod("GIN");
            }

            if (IsSqlite)
            {
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
