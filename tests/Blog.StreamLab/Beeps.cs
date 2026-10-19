namespace Blog.StreamLab;

public readonly record struct Beep(int Code, long Sample, double ArrivedAt);

public sealed class BeepDetector
{
    public const int SampleRate = 48000;
    public const int Codes = 16;
    public const double BaseHz = 1000;
    public const double StepHz = 200;

    private const int Block = 240;
    private const int Window = 2400;
    private const int Refractory = SampleRate / 2;
    private const double OnsetRatio = 4;
    private const double OnsetEnergy = 0.005;

    private readonly float[] _window = new float[Window];
    private readonly float[] _block = new float[Block];
    private readonly double[] _arrivals = new double[Block];
    private int _blockFilled;
    private int _windowFilled;
    private bool _collecting;
    private long _onsetSample;
    private double _onsetArrival;
    private long _quietUntil;
    private double _floor;

    public long Samples { get; private set; }

    public void Feed(ReadOnlySpan<byte> pcm, double arrivedAt, List<Beep> found)
    {
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8) / 32768f;

            _block[_blockFilled] = sample;
            _arrivals[_blockFilled] = arrivedAt;
            _blockFilled++;
            Samples++;

            if (_blockFilled == Block)
            {
                ProcessBlock(found);

                _blockFilled = 0;
            }
        }
    }

    private void ProcessBlock(List<Beep> found)
    {
        var start = Samples - Block;
        var energy = 0.0;

        for (var index = 0; index < Block; index++)
        {
            energy += _block[index] * _block[index];
        }

        energy /= Block;

        if (!_collecting)
        {
            if (energy > _floor * OnsetRatio && energy > OnsetEnergy && start >= _quietUntil)
            {
                _collecting = true;
                _onsetSample = start;
                _onsetArrival = _arrivals[0];
                _windowFilled = 0;
            }
            else
            {
                _floor = _floor * 0.95 + energy * 0.05;

                return;
            }
        }

        var take = Math.Min(Block, Window - _windowFilled);

        Array.Copy(_block, 0, _window, _windowFilled, take);

        _windowFilled += take;

        if (_windowFilled < Window)
        {
            return;
        }

        var best = 0;
        var bestPower = -1.0;

        for (var code = 0; code < Codes; code++)
        {
            var power = Goertzel(BaseHz + StepHz * code);

            if (power > bestPower)
            {
                bestPower = power;
                best = code;
            }
        }

        found.Add(new Beep(best, _onsetSample, _onsetArrival));

        _collecting = false;
        _quietUntil = _onsetSample + Refractory;
    }

    private double Goertzel(double frequency)
    {
        var omega = 2 * Math.PI * frequency / SampleRate;
        var coefficient = 2 * Math.Cos(omega);
        var previous = 0.0;
        var before = 0.0;

        for (var index = 0; index < Window; index++)
        {
            var current = _window[index] + coefficient * previous - before;

            before = previous;
            previous = current;
        }

        return previous * previous + before * before - coefficient * previous * before;
    }
}
