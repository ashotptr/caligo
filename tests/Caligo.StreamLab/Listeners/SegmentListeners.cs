using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace Caligo.StreamLab.Listeners;

public sealed class HlsListener : IListener
{
    private readonly Uri _url;
    private readonly string _format;

    public HlsListener(string url, string format)
    {
        _url = new Uri(url);
        _format = format;
    }

    private sealed record Part(Uri Uri, double Duration);

    private sealed class Segment
    {
        public long Number;
        public Uri? Uri;
        public double Duration;
        public readonly List<Part> Parts = new();
    }

    private sealed class Playlist
    {
        public Uri Address = null!;
        public double TargetDuration = 2;
        public long MediaSequence;
        public Uri? Map;
        public double PartTarget;
        public bool CanBlock;
        public readonly List<Segment> Segments = new();
        public Uri? Variant;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        var playlist = await LoadAsync(_url, cancellationToken);

        if (playlist.Variant is not null)
        {
            playlist = await LoadAsync(playlist.Variant, cancellationToken);
        }

        session.MarkConnected();

        if (playlist.Map is not null)
        {
            await FetchAsync(session, playlist.Map, 0, cancellationToken);
        }

        if (playlist.PartTarget > 0 && playlist.CanBlock)
        {
            await RunPartsAsync(session, playlist, cancellationToken);
        }
        else
        {
            await RunSegmentsAsync(session, playlist, cancellationToken);
        }
    }

    private async Task RunSegmentsAsync(ListenerSession session, Playlist playlist, CancellationToken cancellationToken)
    {
        var complete = playlist.Segments.Where(segment => segment.Uri is not null).ToList();
        var next = complete.Count == 0 ? playlist.MediaSequence : complete[Math.Max(0, complete.Count - 3)].Number;

        while (true)
        {
            var segment = playlist.Segments.FirstOrDefault(item => item.Number == next && item.Uri is not null);

            if (segment is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.2, playlist.TargetDuration / 2)), cancellationToken);

                playlist = await LoadAsync(playlist.Address, cancellationToken);

                if (playlist.Segments.Count > 0 && playlist.Segments[0].Number > next)
                {
                    next = playlist.Segments[0].Number;
                }

                continue;
            }

            await FetchAsync(session, segment.Uri!, segment.Duration, cancellationToken);

            next++;
        }
    }

    private async Task RunPartsAsync(ListenerSession session, Playlist playlist, CancellationToken cancellationToken)
    {
        var positions = playlist.Segments
            .SelectMany(segment => segment.Parts.Select((_, index) => (segment.Number, Index: index)))
            .ToList();
        var (number, part) = positions.Count == 0 ? (playlist.MediaSequence, 0) : positions[Math.Max(0, positions.Count - 3)];

        while (true)
        {
            var segment = playlist.Segments.FirstOrDefault(item => item.Number == number);

            if (segment is not null && part < segment.Parts.Count)
            {
                var item = segment.Parts[part];

                await FetchAsync(session, item.Uri, item.Duration, cancellationToken);

                part++;

                continue;
            }

            if (segment is { Uri: not null })
            {
                if (segment.Parts.Count == 0 && part == 0)
                {
                    await FetchAsync(session, segment.Uri, segment.Duration, cancellationToken);
                }

                number++;
                part = 0;

                continue;
            }

            if (playlist.Segments.Count > 0 && playlist.Segments[0].Number > number)
            {
                number = playlist.Segments[0].Number;
                part = 0;
            }

            var blocking = new UriBuilder(playlist.Address)
            {
                Query = $"_HLS_msn={number}&_HLS_part={part}"
            };

            playlist = await LoadAsync(blocking.Uri, cancellationToken, playlist.Address);
        }
    }

    private async Task FetchAsync(ListenerSession session, Uri uri, double durationSeconds, CancellationToken cancellationToken)
    {
        using var response = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[16384];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            session.Received(read);

            await session.TapAsync(buffer.AsMemory(0, read), _format);
        }

        session.Media(durationSeconds * 1000);
    }

    private static async Task<Playlist> LoadAsync(Uri uri, CancellationToken cancellationToken, Uri? address = null)
    {
        var text = await Http.Client.GetStringAsync(uri, cancellationToken);
        var playlist = new Playlist { Address = address ?? new Uri(uri.GetLeftPart(UriPartial.Path)) };
        var current = new Segment();
        double pendingDuration = 0;
        var expectSegmentUri = false;
        var expectVariantUri = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (expectVariantUri && !line.StartsWith('#'))
            {
                playlist.Variant ??= new Uri(uri, line);
                expectVariantUri = false;

                continue;
            }

            if (expectSegmentUri && !line.StartsWith('#'))
            {
                current.Uri = new Uri(uri, line);
                current.Duration = pendingDuration;
                current.Number = playlist.MediaSequence + playlist.Segments.Count;
                playlist.Segments.Add(current);
                current = new Segment();
                expectSegmentUri = false;

                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                expectVariantUri = true;
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && line.Contains("TYPE=AUDIO") &&
                     Attribute(line, "URI") is { } audio)
            {
                playlist.Variant ??= new Uri(uri, audio);
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                playlist.TargetDuration = Number(line[22..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                playlist.MediaSequence = long.Parse(line[22..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) && Attribute(line, "URI") is { } map)
            {
                playlist.Map = new Uri(uri, map);
            }
            else if (line.StartsWith("#EXT-X-PART-INF:", StringComparison.Ordinal))
            {
                playlist.PartTarget = Number(Attribute(line, "PART-TARGET") ?? "0");
            }
            else if (line.StartsWith("#EXT-X-SERVER-CONTROL:", StringComparison.Ordinal))
            {
                playlist.CanBlock = Attribute(line, "CAN-BLOCK-RELOAD") == "YES";
            }
            else if (line.StartsWith("#EXT-X-PART:", StringComparison.Ordinal) && Attribute(line, "URI") is { } partUri)
            {
                current.Parts.Add(new Part(new Uri(uri, partUri), Number(Attribute(line, "DURATION") ?? "0")));
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                pendingDuration = Number(line[8..].Split(',')[0]);
                expectSegmentUri = true;
            }
        }

        if (current.Parts.Count > 0)
        {
            current.Number = playlist.MediaSequence + playlist.Segments.Count;
            playlist.Segments.Add(current);
        }

        return playlist;
    }

    private static double Number(string text) => double.Parse(text.Trim(), CultureInfo.InvariantCulture);

    private static string? Attribute(string line, string name)
    {
        var start = line.IndexOf(name + "=", StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        start += name.Length + 1;

        if (start < line.Length && line[start] == '"')
        {
            var end = line.IndexOf('"', start + 1);

            return end < 0 ? null : line[(start + 1)..end];
        }

        var stop = line.IndexOf(',', start);

        return stop < 0 ? line[start..] : line[start..stop];
    }
}

public sealed class DashListener : IListener
{
    private readonly Uri _url;

    public DashListener(string url)
    {
        _url = new Uri(url);
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        var text = await Http.Client.GetStringAsync(_url, cancellationToken);
        var document = XDocument.Parse(text);
        XNamespace dash = document.Root!.Name.Namespace;
        var mpd = document.Root;
        var available = DateTime.Parse(mpd.Attribute("availabilityStartTime")!.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var period = mpd.Element(dash + "Period")!;
        var periodStart = Duration(period.Attribute("start")?.Value);
        var adaptation = period.Elements(dash + "AdaptationSet").First();
        var representation = adaptation.Elements(dash + "Representation").First();
        var template = representation.Element(dash + "SegmentTemplate") ?? adaptation.Element(dash + "SegmentTemplate")!;
        var timescale = double.Parse(template.Attribute("timescale")?.Value ?? "1", CultureInfo.InvariantCulture);
        var segmentSeconds = double.Parse(template.Attribute("duration")!.Value, CultureInfo.InvariantCulture) / timescale;
        var startNumber = long.Parse(template.Attribute("startNumber")?.Value ?? "1", CultureInfo.InvariantCulture);
        var id = representation.Attribute("id")!.Value;
        var media = template.Attribute("media")!.Value;
        var initialization = template.Attribute("initialization")?.Value;

        session.MarkConnected();

        if (initialization is not null)
        {
            await FetchAsync(session, new Uri(_url, Fill(initialization, id, 0)), 0, cancellationToken);
        }

        var elapsed = (DateTime.UtcNow - available).TotalSeconds - periodStart;
        var number = Math.Max(startNumber, startNumber + (long)Math.Floor(elapsed / segmentSeconds) - 2);

        while (true)
        {
            var due = available.AddSeconds(periodStart + (number - startNumber + 1) * segmentSeconds);
            var wait = due - DateTime.UtcNow;

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            var uri = new Uri(_url, Fill(media, id, number));

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await FetchAsync(session, uri, segmentSeconds, cancellationToken);

                    break;
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound && attempt < segmentSeconds * 20)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            number++;
        }
    }

    private static async Task FetchAsync(ListenerSession session, Uri uri, double durationSeconds, CancellationToken cancellationToken)
    {
        using var response = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[16384];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            session.Received(read);

            await session.TapAsync(buffer.AsMemory(0, read), "mp4");
        }

        session.Media(durationSeconds * 1000);
    }

    private static string Fill(string template, string id, long number)
    {
        var text = template.Replace("$RepresentationID$", id, StringComparison.Ordinal);
        var start = text.IndexOf("$Number", StringComparison.Ordinal);

        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf('$', start + 1);
        var format = text[(start + 7)..end];
        var value = format.StartsWith('%') && format.EndsWith('d')
            ? number.ToString("D" + format[1..^1].TrimStart('0'), CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

        return text[..start] + value + text[(end + 1)..];
    }

    private static double Duration(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return System.Xml.XmlConvert.ToTimeSpan(text).TotalSeconds;
    }
}
