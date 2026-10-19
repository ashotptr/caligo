using System.Text;

namespace Blog.Catalog.Application.Contents;

public readonly record struct ContentCursor(DateOnly ReleaseDate, int ContentId)
{
    public string Encode()
    {
        var payload = $"{ReleaseDate.DayNumber}:{ContentId}";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                      .TrimEnd('=')
                      .Replace('+', '-')
                      .Replace('/', '_');
    }

    public static ContentCursor? TryDecode(string? cursor)
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
                !int.TryParse(parts[0], out var day) ||
                !int.TryParse(parts[1], out var id) ||
                day < DateOnly.MinValue.DayNumber ||
                day > DateOnly.MaxValue.DayNumber)
            {
                return null;
            }

            return new ContentCursor(DateOnly.FromDayNumber(day), id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
