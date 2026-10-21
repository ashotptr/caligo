using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Engine;

namespace Caligo.Catalog.Infrastructure.Engine
{
    public class CaligoEngineClient : ICaligoEngine
    {
        private static readonly TimeSpan ListingTime = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QueueTime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan AskTime = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan StreamTime = TimeSpan.FromMinutes(30);

        private readonly HttpClient _http;
        private readonly string _mixtapePrefix;
        private readonly ILogger<CaligoEngineClient> _logger;

        public CaligoEngineClient(HttpClient http, IOptions<CatalogOptions> options, ILogger<CaligoEngineClient> logger)
        {
            _http = http;
            _mixtapePrefix = options.Value.MixtapeUrlPrefix;
            _logger = logger;
        }

        public bool IsConfigured => _http.BaseAddress is not null;

        public async Task<IReadOnlySet<string>?> VisibleMixtapeFoldersAsync(
            string? user, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                return null;
            }

            var path = "cgi_bin/mixtape_app" + (string.IsNullOrEmpty(user) ? string.Empty : "?user=" + Uri.EscapeDataString(user));

            try
            {
                using var limit = Limit(ListingTime, cancellationToken);
                using var response = await _http.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"), limit.Token);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
                var folders = new HashSet<string>(StringComparer.Ordinal);

                if (document.RootElement.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
                {
                    foreach (var album in albums.EnumerateArray())
                    {
                        AddFolder(folders, Text(album, "cover"));

                        if (album.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var track in tracks.EnumerateArray())
                            {
                                AddFolder(folders, Text(track, "audio_url"));
                            }
                        }
                    }
                }

                return folders;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Caligo's mixtape_app did not answer");

                return null;
            }
        }

        public IAsyncEnumerable<string> RequestSongAsync(string user, string query, CancellationToken cancellationToken = default) =>
            LinesAsync("cgi_bin/request_song", new StringContent($"{user}|{query}", Encoding.UTF8, "text/plain"), cancellationToken);

        public async Task<RadioQueueResult> QueueToRadioAsync(
            RadioQueueRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var limit = Limit(QueueTime, cancellationToken);
                using var response = await _http.PostAsync(
                    "cgi_bin/radio_control",
                    Json(new { path = request.Path, artist = request.Artist, album = request.Album, title = request.Title }),
                    limit.Token);

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
                var root = document.RootElement;
                var success = root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;

                return new RadioQueueResult(success, Text(root, "message") ?? (success ? "Queued" : "Radio server offline"), Text(root, "track_id"));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Caligo's radio_control did not answer");

                return new RadioQueueResult(false, "Radio server offline", null);
            }
        }

        public async Task<AskAnswer?> AskAsync(string sessionId, string query, CancellationToken cancellationToken = default)
        {
            try
            {
                using var limit = Limit(AskTime, cancellationToken);
                using var response = await _http.PostAsync(
                    "cgi_bin/ask_ai", Json(new { session_id = sessionId, query }), limit.Token);

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));

                if (!document.RootElement.TryGetProperty("answer", out var answer))
                {
                    return null;
                }

                if (answer.ValueKind == JsonValueKind.Array)
                {
                    var songs = answer.EnumerateArray()
                        .Where(song => song.ValueKind == JsonValueKind.Object)
                        .Select(song => new AskSong(Text(song, "artist") ?? string.Empty, Text(song, "title") ?? string.Empty, Text(song, "url")))
                        .ToList();

                    return new AskAnswer(null, songs);
                }

                return new AskAnswer(answer.ValueKind == JsonValueKind.String ? answer.GetString() : answer.ToString(), Array.Empty<AskSong>());
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Caligo's ask_ai did not answer");

                return null;
            }
        }

        public IAsyncEnumerable<string> AddToRotationAsync(RotationRequest request, CancellationToken cancellationToken = default) =>
            LinesAsync(
                "cgi_bin/radio_rotation",
                Json(new
                {
                    folder = request.Folder,
                    file = request.File,
                    artist = request.Artist,
                    album = request.Album,
                    title = request.Title
                }),
                cancellationToken);

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<CaligoTrack>>> GetPlaylistsAsync(
            string user, CancellationToken cancellationToken = default)
        {
            using var limit = Limit(ListingTime, cancellationToken);
            using var response = await _http.PostAsync(
                "cgi_bin/playlist_manager", Json(new { action = "get_playlists", username = user }), limit.Token);

            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            var result = new Dictionary<string, IReadOnlyList<CaligoTrack>>(StringComparer.Ordinal);

            if (document.RootElement.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Object)
            {
                foreach (var playlist in playlists.EnumerateObject())
                {
                    result[playlist.Name] = playlist.Value.ValueKind == JsonValueKind.Array
                        ? playlist.Value.EnumerateArray()
                            .Where(track => track.ValueKind == JsonValueKind.Object)
                            .Select(track => new CaligoTrack(Text(track, "title") ?? string.Empty, Text(track, "audio_url") ?? string.Empty))
                            .Where(track => track.AudioUrl.Length > 0)
                            .ToList()
                        : new List<CaligoTrack>();
                }
            }

            return result;
        }

        private async IAsyncEnumerable<string> LinesAsync(
            string path, HttpContent content, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var limit = Limit(StreamTime, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Caligo did not answer {Path}", path);
                response = null!;
            }

            if (response is null)
            {
                yield return "ERROR: Caligo is not reachable.";

                yield break;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    yield return $"ERROR: Caligo answered {(int)response.StatusCode}.";

                    yield break;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (await reader.ReadLineAsync(limit.Token) is { } line)
                {
                    var trimmed = line.Trim();

                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }
            }
        }

        private void AddFolder(HashSet<string> folders, string? uri)
        {
            if (MixtapePaths.Folder(uri, _mixtapePrefix) is { } folder)
            {
                folders.Add(folder);
            }
        }

        private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static CancellationTokenSource Limit(TimeSpan time, CancellationToken cancellationToken)
        {
            var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            limit.CancelAfter(time);

            return limit;
        }
    }
}
