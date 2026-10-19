using Xunit;

namespace Blog.StreamLab.Tests;

public class SignalTests
{
    private static byte[] LabSignal(double startEpochSeconds, int seconds)
    {
        var samples = seconds * BeepDetector.SampleRate;
        var pcm = new byte[samples * 2];

        for (var index = 0; index < samples; index++)
        {
            var moment = startEpochSeconds + (double)index / BeepDetector.SampleRate;
            var fraction = moment - Math.Floor(moment);
            var value = 0.08 * Math.Sin(2 * Math.PI * 330 * index / BeepDetector.SampleRate);

            if (fraction < 0.06)
            {
                var frequency = BeepDetector.BaseHz + BeepDetector.StepHz * ((long)Math.Floor(moment) % BeepDetector.Codes);

                value += 0.5 * Math.Sin(Math.PI * fraction / 0.06) * Math.Sin(2 * Math.PI * frequency * fraction);
            }

            var level = (short)Math.Round(Math.Clamp(value, -1, 1) * 32767);

            pcm[2 * index] = (byte)level;
            pcm[2 * index + 1] = (byte)(level >> 8);
        }

        return pcm;
    }

    [Fact]
    public void EveryBeepIsFoundWithItsSecondsCode()
    {
        const double start = 1792400000.5;
        var pcm = LabSignal(start, 6);
        var detector = new BeepDetector();
        var found = new List<Beep>();

        for (var offset = 0; offset < pcm.Length; offset += 1920)
        {
            detector.Feed(pcm.AsSpan(offset, Math.Min(1920, pcm.Length - offset)), offset / 96.0, found);
        }

        Assert.Equal(6, found.Count);

        for (var index = 0; index < found.Count; index++)
        {
            var second = (long)Math.Ceiling(start) + index;
            var expectedSample = (long)((second - start) * BeepDetector.SampleRate);

            Assert.Equal((int)(second % BeepDetector.Codes), found[index].Code);
            Assert.InRange(found[index].Sample, expectedSample, expectedSample + 480);
        }
    }

    [Theory]
    [InlineData(5, 1792400005027.0, 27.0)]
    [InlineData(15, 1792400015900.0, 900.0)]
    [InlineData(4, 1792400005010.0, 1010.0)]
    public void TheReferenceGivesTheSourcesOffset(int code, double arrivedAt, double bias)
    {
        Assert.Equal(bias, SourceClock.BiasAt(code, arrivedAt), 6);
    }

    [Theory]
    [InlineData(5, 1792400005127.0, 27.0, 100.0)]
    [InlineData(5, 1792400013027.0, 27.0, 8000.0)]
    [InlineData(14, 1792400015027.0, 27.0, 1000.0)]
    [InlineData(5, 1792400005000.0, 27.0, -27.0)]
    public void LatencyIsMeasuredAgainstTheBeepsOwnSecond(int code, double playedAt, double bias, double latency)
    {
        Assert.Equal(latency, SourceClock.LatencyAt(code, playedAt, bias), 6);
    }
}
