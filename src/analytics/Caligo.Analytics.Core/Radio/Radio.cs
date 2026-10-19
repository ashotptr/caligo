using System.Text.Json;
using System.Text.RegularExpressions;

namespace Caligo.Analytics.Core.Radio;

public record RadioPlay(DateTime StartedAt, string File, string Artist, string Album, string Title, bool IsRequest)
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(30);

    public bool ContinuesFrom(RadioPlay? previous) =>
        previous is not null &&
        File == previous.File &&
        Artist == previous.Artist &&
        Title == previous.Title &&
        StartedAt - previous.StartedAt < RepeatWindow;
}

public record ListenerSample(DateTime SampledAt, string Source, int Listeners);

public interface IRadioStore
{
    bool IsConfigured { get; }

    Task<RadioPlay?> LatestPlayAsync(CancellationToken cancellationToken = default);

    Task WritePlayAsync(RadioPlay play, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RadioPlay>> RecentPlaysAsync(int limit, CancellationToken cancellationToken = default);

    Task WriteListenersAsync(ListenerSample sample, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ListenerSample>> ListenersAsync(
        string source, DateTime from, DateTime to, CancellationToken cancellationToken = default);
}

public static partial class NowPlaying
{
    public const int MaxFieldLength = 512;

    public static RadioPlay? Parse(string? data, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || Text(root, "title") is not { Length: > 0 } title)
            {
                return null;
            }

            return new RadioPlay(
                at,
                Fit(Text(root, "playing")),
                Fit(Text(root, "artist")),
                Fit(Text(root, "album")),
                Fit(title),
                root.TryGetProperty("is_request", out var request) && request.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            var match = Loose().Match(data);

            return match.Success && match.Groups[4].Length > 0
                ? new RadioPlay(
                    at,
                    Fit(match.Groups[1].Value),
                    Fit(match.Groups[2].Value),
                    Fit(match.Groups[3].Value),
                    Fit(match.Groups[4].Value),
                    Requested().IsMatch(data))
                : null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Fit(string? value)
    {
        var text = value ?? string.Empty;

        return text.Length <= MaxFieldLength ? text : text[..MaxFieldLength];
    }

    [GeneratedRegex("\"playing\":\\s*\"(.*)\",\\s*\"artist\":\\s*\"(.*)\",\\s*\"album\":\\s*\"(.*)\",\\s*\"title\":\\s*\"(.*)\",\\s*\"bitrate\"", RegexOptions.Singleline)]
    private static partial Regex Loose();

    [GeneratedRegex("\"is_request\":\\s*true")]
    private static partial Regex Requested();
}
