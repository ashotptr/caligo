namespace Caligo.StreamLab;

public sealed class PlaybackModel
{
    public const double StartupMs = 500;
    public const double ResumeMs = 500;

    private readonly List<(double MediaMs, double DurationMs)> _stallLog = new();
    private double _arrivedMs;
    private double _playheadMs;
    private double _updatedAt;
    private double? _startedAt;
    private bool _stalled;
    private double _stallStartedAt;
    private double _stallMediaMs;

    public int Stalls { get; private set; }

    public double StallMs { get; private set; }

    public double? StartedAt => _startedAt;

    public double ArrivedMs => _arrivedMs;

    public void Arrive(double now, double mediaMs)
    {
        Advance(now);

        _arrivedMs += mediaMs;

        if (_startedAt is null)
        {
            if (_arrivedMs >= StartupMs)
            {
                _startedAt = now;
                _updatedAt = now;
            }

            return;
        }

        if (_stalled && _arrivedMs - _playheadMs >= ResumeMs)
        {
            var duration = now - _stallStartedAt;

            _stalled = false;
            StallMs += duration;
            _stallLog.Add((_stallMediaMs, duration));
        }
    }

    public void Finish(double now)
    {
        Advance(now);

        if (_stalled)
        {
            var duration = now - _stallStartedAt;

            _stalled = false;
            StallMs += duration;
            _stallLog.Add((_stallMediaMs, duration));
        }
    }

    public double? PlayedAt(double mediaMs)
    {
        if (_startedAt is null)
        {
            return null;
        }

        var waited = 0.0;

        foreach (var (stallMediaMs, duration) in _stallLog)
        {
            if (stallMediaMs <= mediaMs)
            {
                waited += duration;
            }
        }

        return _startedAt + mediaMs + waited;
    }

    private void Advance(double now)
    {
        if (_startedAt is null || _stalled)
        {
            _updatedAt = now;

            return;
        }

        var playable = _arrivedMs - _playheadMs;
        var elapsed = now - _updatedAt;

        if (elapsed >= playable)
        {
            _playheadMs = _arrivedMs;
            _stalled = true;
            _stallStartedAt = _updatedAt + playable;
            _stallMediaMs = _playheadMs;
            Stalls++;
        }
        else
        {
            _playheadMs += elapsed;
        }

        _updatedAt = now;
    }
}
