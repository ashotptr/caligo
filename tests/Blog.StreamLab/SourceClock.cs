using System.Net;
using System.Net.Sockets;

namespace Blog.StreamLab;

public sealed class SourceClock : IAsyncDisposable
{
    private const int Window = 15;
    private const double Tolerance = 150;

    private readonly UdpClient _socket;
    private readonly BeepDetector _detector = new();
    private readonly List<double> _biases = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();

    public SourceClock(int port)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _socket.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        _loop = Task.Run(ReceiveAsync);
    }

    public static double WallMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    public double? BiasMs
    {
        get
        {
            lock (_gate)
            {
                if (_biases.Count < 5)
                {
                    return null;
                }

                var sorted = _biases.OrderBy(value => value).ToList();

                return sorted[sorted.Count / 2];
            }
        }
    }

    public async Task<double?> WaitForBiasAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (BiasMs is { } bias)
            {
                return bias;
            }

            await Task.Delay(200);
        }

        return BiasMs;
    }

    public double? LatencyOf(Beep beep, double playedAt) =>
        BiasMs is { } bias ? LatencyAt(beep.Code, playedAt, bias) : null;

    public static double LatencyAt(int code, double playedAt, double biasMs)
    {
        var second = SecondOf(code, playedAt - biasMs + Tolerance);

        return playedAt - (second * 1000.0 + biasMs);
    }

    public static double BiasAt(int code, double arrivedAt) => arrivedAt - SecondOf(code, arrivedAt) * 1000.0;

    private static long SecondOf(int code, double wallMs)
    {
        var second = (long)Math.Floor(wallMs / 1000.0);
        var offset = ((second - code) % BeepDetector.Codes + BeepDetector.Codes) % BeepDetector.Codes;

        return second - offset;
    }

    private async Task ReceiveAsync()
    {
        var found = new List<Beep>();

        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult datagram;

            try
            {
                datagram = await _socket.ReceiveAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            var arrivedAt = WallMs();

            found.Clear();
            _detector.Feed(datagram.Buffer, arrivedAt, found);

            foreach (var beep in found)
            {
                var bias = BiasAt(beep.Code, beep.ArrivedAt);

                lock (_gate)
                {
                    _biases.Add(bias);

                    if (_biases.Count > Window)
                    {
                        _biases.RemoveAt(0);
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket.Dispose();

        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
