using Microsoft.EntityFrameworkCore;
using Blog.Catalog.Application.Contents;

namespace Blog.Catalog.Infrastructure.Persistence
{
    internal sealed class ContentRow
    {
        public int ContentId { get; init; }
        public string Title { get; init; } = string.Empty;
        public float Duration { get; init; }
        public DateOnly ReleaseDate { get; init; }
        public float? AverageRating { get; init; }
        public bool IsMovie { get; init; }
        public bool IsEpisode { get; init; }
        public bool IsSong { get; init; }

        public string Kind =>
            IsMovie ? ContentKinds.Movie
            : IsEpisode ? ContentKinds.Episode
            : IsSong ? ContentKinds.Song
            : ContentKinds.Other;
    }

    internal static class ContentRows
    {
        public static IQueryable<ContentRow> Rows(this StreamingDbContext db) =>
            db.Contents.AsNoTracking().Select(c => new ContentRow
            {
                ContentId = c.ContentId,
                Title = c.Title,
                Duration = c.Duration,
                ReleaseDate = c.ReleaseDate,
                AverageRating = c.AverageRating,
                IsMovie = db.Movies.Any(m => m.ContentId == c.ContentId),
                IsEpisode = db.Episodes.Any(e => e.ContentId == c.ContentId),
                IsSong = db.Songs.Any(s => s.ContentId == c.ContentId)
            });

        public static string FullName(string? firstName, string? lastName) =>
            $"{firstName} {lastName}".Trim();

        public static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
