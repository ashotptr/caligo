using System.Text.Json;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Music;

namespace Caligo.Catalog.Infrastructure.Music
{
    public class CaligoMusicLibrary : IMusicLibrary
    {
        private const string MixtapeFolder = "mixtape";
        private const string MusicFolder = "music";
        private const string AlbumMetadata = "metadata.json";
        private const string RadioPlaylist = "playlist.json";

        private static readonly char[] Separators = { '/', '\\', '\0' };

        private readonly CatalogOptions _options;

        public CaligoMusicLibrary(IOptions<CatalogOptions> options)
        {
            _options = options.Value;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.CaligoPublicRoot) && Directory.Exists(_options.CaligoPublicRoot);

        public Task<MusicLibrary> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() => Read(cancellationToken), cancellationToken);

        private MusicLibrary Read(CancellationToken cancellationToken)
        {
            var releases = new List<LibraryRelease>();
            var problems = new List<string>();

            if (!IsConfigured)
            {
                problems.Add("The Caligo public folder is not configured or does not exist.");

                return new MusicLibrary(releases, problems);
            }

            ReadMixtape(releases, problems, cancellationToken);
            ReadRadioPlaylist(releases, problems, cancellationToken);

            return new MusicLibrary(releases, problems);
        }

        private void ReadMixtape(List<LibraryRelease> releases, List<string> problems, CancellationToken cancellationToken)
        {
            var root = Path.Combine(_options.CaligoPublicRoot!, MixtapeFolder);

            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var folder in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(folder);
                var metadataPath = Path.Combine(folder, AlbumMetadata);

                if (!File.Exists(metadataPath))
                {
                    problems.Add($"mixtape/{name}: no {AlbumMetadata}.");

                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));

                    var album = document.RootElement;
                    var title = Text(album, "albumTitle");
                    var artist = Text(album, "albumArtist");
                    var cover = Text(album, "coverFile");

                    if (title is null || artist is null ||
                        !album.TryGetProperty("tracks", out var listed) || listed.ValueKind != JsonValueKind.Array)
                    {
                        problems.Add($"mixtape/{name}: {AlbumMetadata} is missing albumTitle, albumArtist or tracks.");

                        continue;
                    }

                    var tracks = new List<LibraryTrack>();

                    foreach (var entry in listed.EnumerateArray())
                    {
                        var file = Text(entry, "file");

                        if (file is null)
                        {
                            continue;
                        }

                        var track = Track(
                            folder,
                            file,
                            _options.MixtapeUrlPrefix + name + "/" + file,
                            Text(entry, "title") ?? Path.GetFileNameWithoutExtension(file),
                            artist,
                            $"mixtape/{name}/{file}",
                            problems);

                        if (track is not null)
                        {
                            tracks.Add(track);
                        }
                    }

                    if (tracks.Count > 0)
                    {
                        releases.Add(new LibraryRelease(
                            title,
                            artist,
                            cover is null
                                ? null
                                : _options.MixtapeUrlPrefix + name + "/" + cover,
                            tracks.Count == 1,
                            tracks));
                    }
                }
                catch (JsonException)
                {
                    problems.Add($"mixtape/{name}: {AlbumMetadata} is not valid JSON.");
                }
            }
        }

        private void ReadRadioPlaylist(List<LibraryRelease> releases, List<string> problems, CancellationToken cancellationToken)
        {
            var folder = Path.Combine(_options.CaligoPublicRoot!, MusicFolder);
            var playlistPath = Path.Combine(folder, RadioPlaylist);

            if (!File.Exists(playlistPath))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(playlistPath));

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"music/{RadioPlaylist}: expected a list.");

                    return;
                }

                var groups = new Dictionary<(string Artist, string Album), List<LibraryTrack>>();
                var order = new List<(string Artist, string Album)>();

                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = Text(entry, "file");
                    var artist = Text(entry, "artist");
                    var title = Text(entry, "title");

                    if (file is null || artist is null || title is null)
                    {
                        continue;
                    }

                    var track = Track(
                        folder,
                        file,
                        _options.MusicUrlPrefix + file,
                        title,
                        artist,
                        $"music/{file}",
                        problems);

                    if (track is null)
                    {
                        continue;
                    }

                    var key = (artist, Text(entry, "album") ?? title);

                    if (!groups.TryGetValue(key, out var tracks))
                    {
                        groups[key] = tracks = new List<LibraryTrack>();

                        order.Add(key);
                    }

                    tracks.Add(track);
                }

                releases.AddRange(order.Select(key => new LibraryRelease(key.Album, key.Artist, null, false, groups[key])));
            }
            catch (JsonException)
            {
                problems.Add($"music/{RadioPlaylist} is not valid JSON.");
            }
        }

        private static bool IsPlainFileName(string file) =>
            file is not ("." or "..") &&
            file == Path.GetFileName(file) &&
            file.IndexOfAny(Separators) < 0;

        private static LibraryTrack? Track(
            string folder, string file, string uri, string title, string artist, string label, List<string> problems)
        {
            if (!IsPlainFileName(file))
            {
                problems.Add($"{label}: not a file of this folder.");

                return null;
            }

            var path = Path.Combine(folder, file);

            if (!File.Exists(path))
            {
                problems.Add($"{label}: the file is missing.");

                return null;
            }

            try
            {
                var facts = Mp3Probe.Read(path);

                return new LibraryTrack(
                    uri,
                    title,
                    artist,
                    facts.DurationMinutes,
                    facts.Released,
                    DateOnly.FromDateTime(File.GetLastWriteTimeUtc(path)),
                    facts.TrackNumber,
                    facts.Label,
                    facts.Genres);
            }
            catch (IOException ex)
            {
                problems.Add($"{label}: {ex.Message}");

                return null;
            }
        }

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
    }
}
