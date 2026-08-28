using System.Text;

namespace Blog.Posts.Application.Posts
{
    /// <summary>
    /// Encodes the sort key of the last row on a page. Posts are ordered by
    /// (PublishedDate DESC, Id DESC); Id breaks ties so the ordering is total and the
    /// cursor can never straddle two rows.
    /// </summary>
    public readonly record struct PostCursor(DateTime PublishedDate, int Id)
    {
        public string Encode()
        {
            var payload = $"{PublishedDate.Ticks}:{Id}";

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        }

        /// <summary>
        /// Cursors arrive from the network, so a malformed one is an expected input rather
        /// than an exception: callers treat null as "start from the beginning".
        /// </summary>
        public static PostCursor? TryDecode(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return null;
            }

            try
            {
                var normalized = cursor.Replace('-', '+').Replace('_', '/');

                normalized = (normalized.Length % 4) switch
                {
                    2 => normalized + "==",
                    3 => normalized + "=",
                    0 => normalized,
                    _ => string.Empty
                };

                if (normalized.Length == 0)
                {
                    return null;
                }

                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split(':');

                if (parts.Length != 2 ||
                    !long.TryParse(parts[0], out var ticks) ||
                    !int.TryParse(parts[1], out var id) ||
                    ticks < DateTime.MinValue.Ticks ||
                    ticks > DateTime.MaxValue.Ticks)
                {
                    return null;
                }

                return new PostCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
