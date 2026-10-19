using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Blog.StreamLab.Media;

namespace Blog.StreamLab.Listeners;

public interface IListener
{
    Task RunAsync(ListenerSession session, CancellationToken cancellationToken);
}

public static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = int.MaxValue,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}

public sealed class ProgressiveListener : IListener
{
    private readonly Uri _url;
    private readonly string _format;

    public ProgressiveListener(string url, string format)
    {
        _url = new Uri(url);
        _format = format;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        using var response = await Http.Client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();
        session.MarkConnected();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        IMediaClock clock = _format == "ogg" ? new OggClock() : new Mp3Clock();
        var buffer = new byte[16384];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            session.Received(read);
            session.Media(clock.Feed(buffer.AsSpan(0, read)));

            await session.TapAsync(buffer.AsMemory(0, read), _format);
        }
    }
}

public sealed class RangeListener : IListener
{
    private const int ChunkBytes = 256 * 1024;
    private const double AheadMs = 30000;

    private readonly Func<CancellationToken, Task<Uri>> _resolve;

    public RangeListener(string url) : this(_ => Task.FromResult(new Uri(url)))
    {
    }

    public RangeListener(Func<CancellationToken, Task<Uri>> resolve)
    {
        _resolve = resolve;
    }

    public static RangeListener FirstSongOf(string playlist) => new(async cancellationToken =>
    {
        var playlistUrl = new Uri(playlist);
        var entries = await Http.Client.GetFromJsonAsync<List<JsonElement>>(playlistUrl, cancellationToken)
                      ?? throw new InvalidOperationException("the playlist is empty");
        var file = entries.Select(entry => entry.TryGetProperty("file", out var value) ? value.GetString() : null)
                       .FirstOrDefault(name => name is { Length: > 0 } && name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("the playlist names no MP3 file");

        return new Uri(playlistUrl, string.Join("/", file.Split('/').Select(Uri.EscapeDataString)));
    });

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        var url = await _resolve(cancellationToken);
        var clock = new Mp3Clock();
        var buffer = new byte[16384];
        long offset = 0;
        long? total = null;

        while (total is null || offset < total)
        {
            while (session.Model.StartedAt is { } started &&
                   session.Model.ArrivedMs - (session.Now - started - session.Model.StallMs) > AheadMs)
            {
                await Task.Delay(500, cancellationToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Range = new RangeHeaderValue(offset, offset + ChunkBytes - 1);

            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                break;
            }

            response.EnsureSuccessStatusCode();
            session.MarkConnected();

            total ??= response.Content.Headers.ContentRange?.Length;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                offset += read;
                session.Received(read);
                session.Media(clock.Feed(buffer.AsSpan(0, read)));

                await session.TapAsync(buffer.AsMemory(0, read), "mp3");
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
        }
    }
}

public sealed class FtpListener : IListener
{
    private readonly string _host;
    private readonly string _path;

    public FtpListener(string host, string path)
    {
        _host = host;
        _path = path;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        using var control = new TcpClient();

        await control.ConnectAsync(_host, 21, cancellationToken);

        await using var stream = control.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

        async Task<(int Code, string Text)> ReplyAsync()
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("the FTP server hung up");

                if (line.Length >= 4 && char.IsDigit(line[0]) && line[3] == ' ')
                {
                    return (int.Parse(line[..3]), line[4..]);
                }
            }
        }

        async Task<(int Code, string Text)> CommandAsync(string command)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), cancellationToken);

            return await ReplyAsync();
        }

        static void Expect((int Code, string Text) reply, params int[] codes)
        {
            if (!codes.Contains(reply.Code))
            {
                throw new IOException($"FTP said {reply.Code} {reply.Text}");
            }
        }

        Expect(await ReplyAsync(), 220);

        var user = await CommandAsync("USER anonymous");

        if (user.Code == 331)
        {
            Expect(await CommandAsync("PASS lab@localhost"), 230);
        }
        else
        {
            Expect(user, 230);
        }

        Expect(await CommandAsync("TYPE I"), 200);

        var passive = await CommandAsync("PASV");

        Expect(passive, 227);

        var numbers = passive.Text[(passive.Text.IndexOf('(') + 1)..passive.Text.IndexOf(')')].Split(',').Select(int.Parse).ToArray();
        var address = new IPAddress(numbers[..4].Select(value => (byte)value).ToArray());
        var port = numbers[4] * 256 + numbers[5];

        using var data = new TcpClient();

        await data.ConnectAsync(address, port, cancellationToken);
        Expect(await CommandAsync("RETR " + _path), 125, 150);
        session.MarkConnected();

        await using var download = data.GetStream();

        var clock = new Mp3Clock();
        var buffer = new byte[65536];
        int read;

        while ((read = await download.ReadAsync(buffer, cancellationToken)) > 0)
        {
            session.Received(read);
            session.Media(clock.Feed(buffer.AsSpan(0, read)));

            await session.TapAsync(buffer.AsMemory(0, read), "mp3");
        }
    }
}
