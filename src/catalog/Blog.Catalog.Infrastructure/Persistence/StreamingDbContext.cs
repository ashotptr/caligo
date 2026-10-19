using System.Collections;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Blog.Catalog.Domain.Activity;
using Blog.Catalog.Domain.Contents;
using Blog.Catalog.Domain.Media;
using Blog.Catalog.Domain.Music;
using Blog.Catalog.Domain.Users;
using Blog.Catalog.Infrastructure.Schema;

namespace Blog.Catalog.Infrastructure.Persistence
{
    public class StreamingDbContext : DbContext
    {
        private const string Timestamp = "timestamp without time zone";

        public StreamingDbContext(DbContextOptions<StreamingDbContext> options)
            : base(options)
        {
        }

        public DbSet<Content> Contents => Set<Content>();
        public DbSet<Movie> Movies => Set<Movie>();
        public DbSet<Series> Series => Set<Series>();
        public DbSet<Season> Seasons => Set<Season>();
        public DbSet<Episode> Episodes => Set<Episode>();
        public DbSet<Subtitle> Subtitles => Set<Subtitle>();
        public DbSet<Genre> Genres => Set<Genre>();
        public DbSet<Language> Languages => Set<Language>();
        public DbSet<ProductionCompany> ProductionCompanies => Set<ProductionCompany>();
        public DbSet<DistributingCompany> DistributingCompanies => Set<DistributingCompany>();
        public DbSet<ContentGenre> ContentGenres => Set<ContentGenre>();
        public DbSet<ContentLanguage> ContentLanguages => Set<ContentLanguage>();
        public DbSet<ContentDistributingCompany> ContentDistributingCompanies => Set<ContentDistributingCompany>();
        public DbSet<Actor> Actors => Set<Actor>();
        public DbSet<ContentActor> ContentActors => Set<ContentActor>();

        public DbSet<StreamingUser> Users => Set<StreamingUser>();
        public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<BlogUser> BlogUsers => Set<BlogUser>();

        public DbSet<UserContentInteraction> Interactions => Set<UserContentInteraction>();
        public DbSet<WatchHistory> WatchHistory => Set<WatchHistory>();
        public DbSet<Comment> Comments => Set<Comment>();
        public DbSet<NotificationReason> NotificationReasons => Set<NotificationReason>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<CurrentContentRecommendation> Recommendations => Set<CurrentContentRecommendation>();

        public DbSet<Artist> Artists => Set<Artist>();
        public DbSet<Album> Albums => Set<Album>();
        public DbSet<SingleRelease> Singles => Set<SingleRelease>();
        public DbSet<Song> Songs => Set<Song>();
        public DbSet<SongArtist> SongArtists => Set<SongArtist>();
        public DbSet<AlbumArtist> AlbumArtists => Set<AlbumArtist>();
        public DbSet<AlbumSong> AlbumSongs => Set<AlbumSong>();
        public DbSet<SingleArtist> SingleArtists => Set<SingleArtist>();
        public DbSet<SingleSong> SingleSongs => Set<SingleSong>();

        public DbSet<ContentMedia> ContentMedia => Set<ContentMedia>();
        public DbSet<Rendition> Renditions => Set<Rendition>();
        public DbSet<SubtitleFile> SubtitleFiles => Set<SubtitleFile>();
        public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
        public DbSet<PlaybackPosition> PlaybackPositions => Set<PlaybackPosition>();

        public DbSet<PopularContentRow> PopularContent => Set<PopularContentRow>();
        public DbSet<ActiveUserRow> ActiveUsers => Set<ActiveUserRow>();
        public DbSet<EngagingContentRow> EngagingContent => Set<EngagingContentRow>();

        public static float CalculateSimilarity(int content1Id, int content2Id) =>
            throw new NotSupportedException("calculatesimilarity runs in the database.");

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.HasDefaultSchema(StreamingSchema.Name);

            model.HasDbFunction(
                    typeof(StreamingDbContext).GetMethod(nameof(CalculateSimilarity), new[] { typeof(int), typeof(int) })!)
                .HasName("calculatesimilarity")
                .HasSchema(StreamingSchema.Name);

            model.Entity<Content>().ToTable("content").HasKey(e => e.ContentId);
            model.Entity<Movie>().ToTable("movie").HasKey(e => e.MovieId);
            model.Entity<Series>().ToTable("series").HasKey(e => e.SeriesId);
            model.Entity<Season>().ToTable("season").HasKey(e => new { e.SeriesId, e.SeasonNumber });
            model.Entity<Episode>().ToTable("episode")
                .HasKey(e => new { e.SeriesId, e.SeasonNumber, e.EpisodeNumber, e.ContentId });
            model.Entity<Subtitle>().ToTable("subtitle").HasKey(e => e.SubtitleId);
            model.Entity<Genre>().ToTable("genres").HasKey(e => e.GenreId);
            model.Entity<Language>().ToTable("languages").HasKey(e => e.LanguageId);
            model.Entity<ProductionCompany>().ToTable("productioncompanies").HasKey(e => e.ProductionCompanyId);
            model.Entity<DistributingCompany>().ToTable("distributingcompanies").HasKey(e => e.DistributingCompanyId);
            model.Entity<ContentGenre>().ToTable("contentgenres").HasKey(e => new { e.ContentId, e.GenreId });
            model.Entity<ContentLanguage>().ToTable("contentlanguages").HasKey(e => new { e.ContentId, e.LanguageId });
            model.Entity<ContentDistributingCompany>().ToTable("contentdistributingcompanies")
                .HasKey(e => new { e.ContentId, e.DistributingCompanyId });
            model.Entity<Actor>().ToTable("actor").HasKey(e => e.ActorId);
            model.Entity<ContentActor>().ToTable("contentactor").HasKey(e => new { e.ContentId, e.ActorId });

            model.Entity<StreamingUser>().ToTable("user").HasKey(e => e.UserId);
            model.Entity<SubscriptionPlan>().ToTable("subscriptionplan").HasKey(e => e.SubscriptionPlanId);
            model.Entity<Payment>(payment =>
            {
                payment.ToTable("payments").HasKey(e => e.PaymentId);
                payment.Property(e => e.Date).HasColumnType(Timestamp);
            });
            model.Entity<BlogUser>(link =>
            {
                link.ToTable("bloguser").HasKey(e => e.UserId);
                link.Property(e => e.UserId).ValueGeneratedNever();
            });

            model.Entity<UserContentInteraction>(interaction =>
            {
                interaction.ToTable("usercontentinteraction").HasKey(e => new { e.ContentId, e.UserId });
                interaction.Property(e => e.Liked)
                    .HasColumnType("bit(1)")
                    .HasConversion(new ValueConverter<bool, BitArray>(
                        liked => new BitArray(1, liked),
                        bits => bits.Length > 0 && bits[0]));
            });
            model.Entity<WatchHistory>(watch =>
            {
                watch.ToTable("watchhistory").HasKey(e => e.WatchedContentId);
                watch.Property(e => e.WatchedContentId).HasColumnName("watched-content_id");
                watch.Property(e => e.Date).HasColumnType(Timestamp);
            });
            model.Entity<Comment>(comment =>
            {
                comment.ToTable("comment").HasKey(e => e.CommentId);
                comment.Property(e => e.Text).HasColumnName("comment");
            });
            model.Entity<NotificationReason>().ToTable("notification_reasons").HasKey(e => e.ReasonId);
            model.Entity<Notification>(notification =>
            {
                notification.ToTable("notifications").HasKey(e => e.NotificationId);
                notification.Property(e => e.NotificationDate).HasColumnType(Timestamp);
            });
            model.Entity<CurrentContentRecommendation>(recommendation =>
            {
                recommendation.ToTable("currentcontentrecommendations").HasKey(e => e.RecommendationId);
                recommendation.Property(e => e.RecommendationDate).HasColumnType(Timestamp);
            });

            model.Entity<Artist>().ToTable("artist").HasKey(e => e.ArtistId);
            model.Entity<Album>().ToTable("album").HasKey(e => e.AlbumId);
            model.Entity<SingleRelease>().ToTable("single").HasKey(e => e.SingleId);
            model.Entity<Song>().ToTable("song").HasKey(e => e.SongId);
            model.Entity<SongArtist>().ToTable("songartist").HasKey(e => new { e.SongId, e.ArtistId });
            model.Entity<AlbumArtist>().ToTable("albumartist").HasKey(e => new { e.AlbumId, e.ArtistId });
            model.Entity<AlbumSong>().ToTable("albumsong").HasKey(e => new { e.AlbumId, e.SongId });
            model.Entity<SingleArtist>().ToTable("singleartist").HasKey(e => new { e.SingleId, e.ArtistId });
            model.Entity<SingleSong>().ToTable("singlesong").HasKey(e => new { e.SingleId, e.SongId });

            model.Entity<ContentMedia>(media =>
            {
                media.ToTable("contentmedia").HasKey(e => e.ContentId);
                media.Property(e => e.ContentId).ValueGeneratedNever();
                media.Property(e => e.RequestedAt).HasColumnType(Timestamp);
                media.Property(e => e.ClaimedUntil).HasColumnType(Timestamp);
                media.Property(e => e.PackagedAt).HasColumnType(Timestamp);
                media.Ignore(e => e.IsPlayable);
                media.Ignore(e => e.IsBusy);
            });
            model.Entity<Rendition>().ToTable("rendition").HasKey(e => e.RenditionId);
            model.Entity<SubtitleFile>(file =>
            {
                file.ToTable("subtitlefile").HasKey(e => e.SubtitleId);
                file.Property(e => e.SubtitleId).ValueGeneratedNever();
            });
            model.Entity<PlaybackSession>(session =>
            {
                session.ToTable("playbacksession").HasKey(e => e.SessionId);
                session.Property(e => e.SessionId).ValueGeneratedNever();
                session.Property(e => e.StartedAt).HasColumnType(Timestamp);
                session.Property(e => e.ExpiresAt).HasColumnType(Timestamp);
                session.Property(e => e.EndedAt).HasColumnType(Timestamp);
                session.Property(e => e.CompletedAt).HasColumnType(Timestamp);
                session.Ignore(e => e.IsCompleted);
            });
            model.Entity<PlaybackPosition>(position =>
            {
                position.ToTable("playbackposition").HasKey(e => new { e.UserId, e.ContentId });
                position.Property(e => e.UpdatedAt).HasColumnType(Timestamp);
            });

            model.Entity<PopularContentRow>().HasNoKey().ToView("popularcontent");
            model.Entity<ActiveUserRow>().HasNoKey().ToView("activeusers");
            model.Entity<EngagingContentRow>().HasNoKey().ToView("engagingcontent");

            foreach (var entity in model.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetColumnName() == property.Name)
                    {
                        property.SetColumnName(SnakeCase(property.Name));
                    }
                }
            }

            model.Entity<PopularContentRow>().Property(e => e.TotalViews).HasColumnName("totalviews");
        }

        internal static string SnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 4);

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (char.IsUpper(c) && i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }
    }

    public class PopularContentRow
    {
        public int ContentId { get; set; }

        public string Title { get; set; } = string.Empty;

        public long TotalViews { get; set; }
    }

    public class ActiveUserRow
    {
        public int UserId { get; set; }

        public string? FullName { get; set; }

        public long TotalViews { get; set; }

        public long TotalInteractions { get; set; }

        public long CommentsLeft { get; set; }
    }

    public class EngagingContentRow
    {
        public int ContentId { get; set; }

        public string Title { get; set; } = string.Empty;

        public long TotalViews { get; set; }

        public long TotalInteractions { get; set; }

        public long CommentsLeft { get; set; }

        public decimal? EngagementRatio { get; set; }
    }
}
