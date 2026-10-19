namespace Caligo.Catalog.Application.Media;

public static class MediaPaths
{
    public const int MaxLength = 200;
    public const int MaxFileNameLength = 255;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".mpd"] = "application/dash+xml",
        [".m4s"] = "video/iso.segment",
        [".mp4"] = "video/mp4",
        [".vtt"] = "text/vtt; charset=utf-8"
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".mpg", ".mpeg"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vtt", ".srt"
    };

    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength)
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.')
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static string? ContentType(string path) =>
        ContentTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : null;

    public static bool IsPlainFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength || name[0] == '.' || name != name.Trim())
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is '/' or '\\' or ':' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSourceFile(string? name) =>
        IsPlainFileName(name) && SourceExtensions.Contains(Path.GetExtension(name!));

    public static bool IsSubtitleFile(string? name) =>
        IsPlainFileName(name) && SubtitleExtensions.Contains(Path.GetExtension(name!));
}

public readonly record struct Sidecar(string File, string Tag);

public static class Sidecars
{
    public static IReadOnlyList<Sidecar> Of(string sourceFile, IEnumerable<string> fileNames)
    {
        var prefix = Path.GetFileNameWithoutExtension(sourceFile) + ".";
        var found = new List<Sidecar>();

        foreach (var name in fileNames)
        {
            if (!MediaPaths.IsSubtitleFile(name))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(name);

            if (stem.Length <= prefix.Length || !stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = stem[prefix.Length..].Trim();

            if (tag.Length > 0 && !tag.Contains('.'))
            {
                found.Add(new Sidecar(name, tag));
            }
        }

        return found.OrderBy(s => s.Tag, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.File, StringComparer.Ordinal).ToList();
    }
}

public static class LanguageTags
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arabic"] = "ar",
        ["Armenian"] = "hy",
        ["Chinese"] = "zh",
        ["Croatian"] = "hr",
        ["Czech"] = "cs",
        ["Danish"] = "da",
        ["Dutch"] = "nl",
        ["English"] = "en",
        ["Finnish"] = "fi",
        ["French"] = "fr",
        ["Georgian"] = "ka",
        ["German"] = "de",
        ["Greek"] = "el",
        ["Hebrew"] = "he",
        ["Hindi"] = "hi",
        ["Hungarian"] = "hu",
        ["Italian"] = "it",
        ["Japanese"] = "ja",
        ["Korean"] = "ko",
        ["Norwegian"] = "no",
        ["Persian"] = "fa",
        ["Polish"] = "pl",
        ["Portuguese"] = "pt",
        ["Romanian"] = "ro",
        ["Russian"] = "ru",
        ["Serbian"] = "sr",
        ["Spanish"] = "es",
        ["Swedish"] = "sv",
        ["Turkish"] = "tr",
        ["Ukrainian"] = "uk"
    };

    public static string? For(string? languageName) =>
        languageName is not null && Known.TryGetValue(languageName.Trim(), out var tag) ? tag : null;
}
