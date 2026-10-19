using System.Diagnostics;

namespace Caligo.StreamLab;

public sealed class ProbeDecoder : IAsyncDisposable
{
    private const int Chunk = 1920;

    private readonly string _ffmpeg;
    private readonly SourceClock _clock;
    private readonly PlaybackModel _model = new();
    private readonly BeepDetector _detector = new();
    private readonly List<Beep> _beeps = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private Process? _process;
    private Task? _reader;
    private ListenerSession? _forward;
    private bool _broken;

    public ProbeDecoder(string ffmpeg, SourceClock clock)
    {
        _ffmpeg = ffmpeg;
        _clock = clock;
    }

    public void StartUrl(string url, IEnumerable<string> inputOptions, ListenerSession session)
    {
        _forward = session;

        Start(inputOptions.Concat(new[] { "-i", url }), feedStdin: false);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, string format)
    {
        if (_broken || data.Length == 0)
        {
            return;
        }

        await _writing.WaitAsync();

        try
        {
            if (_process is null)
            {
                Start(new[] { "-f", format, "-i", "pipe:0" }, feedStdin: true);
            }

            await _process!.StandardInput.BaseStream.WriteAsync(data);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        catch (Exception)
        {
            _broken = true;
        }
        finally
        {
            _writing.Release();
        }
    }

    public async Task FinishAsync(ListenerSession session)
    {
        if (_process is not null)
        {
            try
            {
                if (_process.StartInfo.RedirectStandardInput)
                {
                    _process.StandardInput.Close();
                }

                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }
        }

        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(3000));
        }

        lock (_beeps)
        {
            _model.Finish(SourceClock.WallMs());

            foreach (var beep in _beeps)
            {
                if (_model.PlayedAt(beep.Sample / 48.0) is not { } played || _clock.LatencyOf(beep, played) is not { } latency)
                {
                    continue;
                }

                session.Latencies.Add((played, latency, _clock.LatencyOf(beep, beep.ArrivedAt)));
            }
        }
    }

    private void Start(IEnumerable<string> input, bool feedStdin)
    {
        var info = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardInput = feedStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-probesize", "2048",
                     "-analyzeduration", "0" }.Concat(input).Concat(new[] { "-vn", "-ac", "1", "-ar", "48000",
                     "-flush_packets", "1", "-f", "s16le", "pipe:1" }))
        {
            info.ArgumentList.Add(argument);
        }

        _process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[Chunk * 4];
        var found = new List<Beep>();
        var carry = 0;
        var stream = _process!.StandardOutput.BaseStream;

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(carry));
            }
            catch (Exception)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            var arrivedAt = SourceClock.WallMs();
            var total = carry + read;
            var usable = total - total % 2;

            found.Clear();

            lock (_beeps)
            {
                _detector.Feed(buffer.AsSpan(0, usable), arrivedAt, found);
                _model.Arrive(arrivedAt, usable / 2 / 48.0);
                _beeps.AddRange(found);
            }

            if (_forward is not null)
            {
                _forward.MarkConnected();
                _forward.Touch();
                _forward.Media(usable / 2 / 48.0);
            }

            carry = total - usable;

            if (carry > 0)
            {
                buffer[0] = buffer[usable];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(1000));
        }

        _process?.Dispose();
        _writing.Dispose();
    }
}
