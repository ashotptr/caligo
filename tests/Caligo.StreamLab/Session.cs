using System.Diagnostics;

namespace Caligo.StreamLab;

public sealed class ListenerSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    public ListenerSession(int index, ProbeDecoder? probe)
    {
        Index = index;
        Probe = probe;
    }

    public int Index { get; }

    public ProbeDecoder? Probe { get; }

    public PlaybackModel Model { get; } = new();

    public double Now => _clock.Elapsed.TotalMilliseconds;

    public bool Connected { get; private set; }

    public double? FirstByteMs { get; private set; }

    public long Bytes { get; private set; }

    public string? Error { get; private set; }

    public double? StartupOverrideMs { get; set; }

    public double? FirstByteOverrideMs { get; set; }

    public int? StallsOverride { get; set; }

    public double? StallMsOverride { get; set; }

    public double? MediaOverrideMs { get; set; }

    public List<(double WallMs, double LatencyMs, double? DataLatencyMs)> Latencies { get; } = new();

    public void MarkConnected() => Connected = true;

    public void Received(long count)
    {
        lock (_gate)
        {
            if (count > 0 && FirstByteMs is null)
            {
                FirstByteMs = Now;
            }

            Bytes += count;
        }
    }

    public void Touch()
    {
        lock (_gate)
        {
            FirstByteMs ??= Now;
        }
    }

    public void Media(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            Model.Arrive(Now, milliseconds);
        }
    }

    public ValueTask TapAsync(ReadOnlyMemory<byte> data, string format) =>
        Probe is null ? ValueTask.CompletedTask : Probe.WriteAsync(data, format);

    public void Fail(Exception error)
    {
        if (error is OperationCanceledException)
        {
            return;
        }

        Error ??= error.GetBaseException().Message;
    }

    public void Fail(string error) => Error ??= error;

    public void Finish()
    {
        lock (_gate)
        {
            Model.Finish(Now);
        }
    }
}
