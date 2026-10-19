using Blog.StreamLab.Listeners;
using Blog.StreamLab.Media;
using Xunit;

namespace Blog.StreamLab.Tests;

public class MediaTests
{
    private static byte[] Mp3Frames(int count)
    {
        var frame = new byte[384];

        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x94;
        frame[3] = 0x44;

        return Enumerable.Range(0, count).SelectMany(_ => frame).ToArray();
    }

    [Fact]
    public void Mp3FramesAreTimedAcrossChunkBoundariesAndTagsAreSkipped()
    {
        var tag = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 20 }.Concat(new byte[20]).ToArray();
        var stream = tag.Concat(Mp3Frames(50)).ToArray();
        var clock = new Mp3Clock();
        var total = 0.0;

        for (var offset = 0; offset < stream.Length; offset += 1000)
        {
            total += clock.Feed(stream.AsSpan(offset, Math.Min(1000, stream.Length - offset)));
        }

        Assert.Equal(50 * 24.0, total, 6);
    }

    [Fact]
    public void OggOpusPagesWrittenForTheProbeTimeLikeTheRelaysOwn()
    {
        var writer = new OggOpusWriter();
        var packet = new byte[] { 0xF8 }.Concat(new byte[60]).ToArray();
        var pages = new List<byte>();

        for (var index = 0; index < 50; index++)
        {
            pages.AddRange(writer.Packet(packet, 2));
        }

        var clock = new OggClock();
        var media = clock.Feed(pages.ToArray());

        Assert.Equal(49 * 20.0, media, 6);
        Assert.Equal(960, Opus.Samples(packet));
        Assert.Equal(1920, Opus.Samples(new byte[] { 0xF9, 0, 0 }));
        Assert.Equal(480, Opus.Samples(new byte[] { 0x00 }));
    }

    [Fact]
    public void VarintsRoundTrip()
    {
        foreach (var value in new ulong[] { 0, 63, 64, 16383, 16384, 1073741823, 1073741824, 0x3f5153300d0a0d0aUL })
        {
            var bytes = Varint.Of(value);
            var position = 0;

            Assert.True(Varint.TryRead(bytes, ref position, out var read));
            Assert.Equal(value, read);
            Assert.Equal(bytes.Length, position);
        }
    }

    [Fact]
    public async Task AGroupStreamDeliversEveryFrameEvenWhenSplit()
    {
        var group = new List<byte>();

        group.AddRange(Varint.Of(0));
        group.AddRange(Varint.Message(Varint.Of(1), Varint.Of(1792400000), Varint.Of(0)));

        for (var index = 0; index < 50; index++)
        {
            group.AddRange(Varint.Of(index == 0 ? 0UL : 1920UL));
            group.AddRange(Varint.Of(40));
            group.AddRange(new byte[] { 0xF8 }.Concat(new byte[39]));
        }

        var session = new ListenerSession(0, null);
        var sink = new OpusSink(session);
        var reader = new GroupReader();
        var bytes = group.ToArray();

        for (var offset = 0; offset < bytes.Length; offset += 7)
        {
            await reader.FeedAsync(bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)), sink);
        }

        Assert.Equal(50 * 20.0, session.Model.ArrivedMs, 6);
    }
}
