using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blog.StreamLab.Listeners;

public sealed class FfmpegListener : IListener
{
    private static readonly string[] LowDelay = { "-fflags", "nobuffer", "-probesize", "2048", "-analyzeduration", "0" };

    private readonly string _ffmpeg;
    private readonly string _url;
    private readonly string[] _input;

    public FfmpegListener(string ffmpeg, string url, params string[] input)
    {
        _ffmpeg = ffmpeg;
        _url = url;
        _input = input;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        if (session.Probe is not null)
        {
            session.Probe.StartUrl(_url, LowDelay.Concat(_input), session);

            await Task.Delay(Timeout.Infinite, cancellationToken);

            return;
        }

        var info = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats" }.Concat(LowDelay)
                     .Concat(_input).Concat(new[] { "-i", _url, "-map", "0:a", "-c", "copy", "-f", "null", "-",
                         "-progress", "pipe:1", "-stats_period", "0.2" }))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        var errors = new StringBuilder();

        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text && errors.Length < 400)
            {
                errors.AppendLine(text);
            }
        };
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        });

        long lastTime = 0;
        long lastSize = 0;

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) && time > lastTime)
            {
                session.MarkConnected();
                session.Media((time - lastTime) / 1000.0);

                lastTime = time;
            }
            else if (line.StartsWith("total_size=", StringComparison.Ordinal) &&
                     long.TryParse(line[11..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > lastSize)
            {
                session.Received(size - lastSize);

                lastSize = size;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);

        if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
        {
            session.Fail("ffmpeg: " + errors.ToString().Trim());
        }
    }
}

public sealed class ChromeHost : IAsyncDisposable
{
    private const int Port = 9222;

    private readonly Process _process;
    private readonly string _folder;

    private ChromeHost(Process process, string folder)
    {
        _process = process;
        _folder = folder;
    }

    public static async Task<ChromeHost> StartAsync(string browser, string trustedOrigin, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "lab-chrome-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(browser)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[]
                 {
                     "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--no-first-run",
                     "--no-default-browser-check", "--disable-extensions", "--mute-audio",
                     "--autoplay-policy=no-user-gesture-required", "--remote-debugging-port=" + Port,
                     "--user-data-dir=" + folder, "--unsafely-treat-insecure-origin-as-secure=" + trustedOrigin, "about:blank"
                 })
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("the browser did not start");

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new ChromeHost(process, folder);
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Http.Client.GetStringAsync($"http://127.0.0.1:{Port}/json/version", cancellationToken);

                return host;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        await host.DisposeAsync();

        throw new TimeoutException("the browser never opened its DevTools port");
    }

    public async Task<CdpTab> OpenAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{Port}/json/new?{Uri.EscapeDataString(url)}");
        using var response = await Http.Client.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var target = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new InvalidOperationException("no target");
        var tab = new CdpTab(target["id"]!.GetValue<string>(), Port);

        await tab.ConnectAsync(target["webSocketDebuggerUrl"]!.GetValue<string>(), cancellationToken);

        return tab;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(new CancellationTokenSource(5000).Token);
        }
        catch (Exception)
        {
        }

        _process.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}

public sealed class CdpTab : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly string _id;
    private readonly int _port;
    private int _sequence;

    public CdpTab(string id, int port)
    {
        _id = id;
        _port = port;
    }

    public Task ConnectAsync(string address, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(new Uri(address), cancellationToken);

    public async Task<string?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _sequence);
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = "Runtime.evaluate",
            ["params"] = new JsonObject { ["expression"] = expression, ["returnByValue"] = true }
        };

        await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[1 << 20];

        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                length += result.Count;
            }
            while (!result.EndOfMessage && length < buffer.Length);

            using var reply = JsonDocument.Parse(buffer.AsMemory(0, length));

            if (!reply.RootElement.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id)
            {
                continue;
            }

            if (reply.RootElement.TryGetProperty("result", out var outer) && outer.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", new CancellationTokenSource(2000).Token);
        }
        catch (Exception)
        {
        }

        _socket.Dispose();

        try
        {
            await Http.Client.GetStringAsync($"http://127.0.0.1:{_port}/json/close/{_id}");
        }
        catch (Exception)
        {
        }
    }
}

public sealed class BrowserListener : IListener
{
    private readonly Func<CancellationToken, Task<ChromeHost>> _browser;
    private readonly SourceClock _clock;
    private readonly string _page;
    private readonly string _endpoint;
    private readonly int _seconds;

    public BrowserListener(Func<CancellationToken, Task<ChromeHost>> browser, SourceClock clock, string page, string endpoint, int seconds)
    {
        _browser = browser;
        _clock = clock;
        _page = page;
        _endpoint = endpoint;
        _seconds = seconds;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        var chrome = await _browser(cancellationToken);
        var started = SourceClock.WallMs();

        await using var tab = await chrome.OpenAsync(
            $"{_page}?url={Uri.EscapeDataString(_endpoint)}&seconds={_seconds}", cancellationToken);

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(_seconds + 20));
        string? text = null;

        while (!grace.IsCancellationRequested)
        {
            text = await tab.EvaluateAsync("window.report ? JSON.stringify(window.report) : ''", grace.Token);

            if (!string.IsNullOrEmpty(text))
            {
                break;
            }

            await Task.Delay(500, grace.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        if (string.IsNullOrEmpty(text))
        {
            session.Fail("the probe page never reported");

            return;
        }

        using var report = JsonDocument.Parse(text);
        var root = report.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            session.Fail("browser: " + error.GetString());
        }

        if (root.TryGetProperty("firstByteMs", out var firstByte))
        {
            session.MarkConnected();
        }

        var samples = root.GetProperty("samples").EnumerateArray().ToList();

        if (samples.Count > 0)
        {
            var first = samples[0];
            var last = samples[^1];

            session.Received(last.GetProperty("bytes").GetInt64());
            session.StallsOverride = (int)(last.GetProperty("events").GetDouble() - first.GetProperty("events").GetDouble());
            session.StallMsOverride = (last.GetProperty("concealed").GetDouble() - first.GetProperty("concealed").GetDouble()) / 48.0;
            session.MediaOverrideMs = last.GetProperty("received").GetDouble() / 48.0;
        }

        if (root.TryGetProperty("startupMs", out var startup))
        {
            session.StartupOverrideMs = startup.GetDouble() + (root.GetProperty("startedAt").GetDouble() - started);
        }

        if (firstByte.ValueKind == JsonValueKind.Number)
        {
            session.FirstByteOverrideMs = firstByte.GetDouble() + (root.GetProperty("startedAt").GetDouble() - started);
        }

        foreach (var beep in root.GetProperty("beeps").EnumerateArray())
        {
            var wall = beep.GetProperty("wallMs").GetDouble();
            var heard = new Beep(beep.GetProperty("code").GetInt32(), 0, wall);

            if (_clock.LatencyOf(heard, wall) is { } latency)
            {
                session.Latencies.Add((wall, latency, null));
            }
        }
    }
}
