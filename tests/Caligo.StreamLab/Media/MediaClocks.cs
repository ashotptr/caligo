namespace Caligo.StreamLab.Media;

public interface IMediaClock
{
    double Feed(ReadOnlySpan<byte> data);
}

public sealed class Mp3Clock : IMediaClock
{
    private static readonly int[] Mpeg1Rates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private static readonly int[] Mpeg2Rates = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    private static readonly int[] SampleRates = { 44100, 48000, 32000, 0 };

    private readonly List<byte> _pending = new();
    private long _skip;

    public double Feed(ReadOnlySpan<byte> data)
    {
        if (_skip > 0)
        {
            var skipped = (int)Math.Min(_skip, data.Length);

            data = data[skipped..];
            _skip -= skipped;
        }

        _pending.AddRange(data.ToArray());

        var media = 0.0;
        var position = 0;
        var buffer = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);

        while (position + 10 <= buffer.Length)
        {
            if (buffer[position] == (byte)'I' && buffer[position + 1] == (byte)'D' && buffer[position + 2] == (byte)'3')
            {
                var size = (buffer[position + 6] & 0x7F) << 21 | (buffer[position + 7] & 0x7F) << 14 |
                           (buffer[position + 8] & 0x7F) << 7 | (buffer[position + 9] & 0x7F);
                var total = 10 + size;

                if (position + total > buffer.Length)
                {
                    _skip = position + total - buffer.Length;
                    position = buffer.Length;

                    break;
                }

                position += total;

                continue;
            }

            if (buffer[position] != 0xFF || (buffer[position + 1] & 0xE0) != 0xE0)
            {
                position++;

                continue;
            }

            var version = buffer[position + 1] >> 3 & 0x03;
            var layer = buffer[position + 1] >> 1 & 0x03;
            var rateIndex = buffer[position + 2] >> 4;
            var sampleIndex = buffer[position + 2] >> 2 & 0x03;
            var padding = buffer[position + 2] >> 1 & 0x01;

            if (version == 1 || layer != 1 || rateIndex is 0 or 15 || sampleIndex == 3)
            {
                position++;

                continue;
            }

            var mpeg1 = version == 3;
            var sampleRate = SampleRates[sampleIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            var bitrate = (mpeg1 ? Mpeg1Rates : Mpeg2Rates)[rateIndex] * 1000;
            var samples = mpeg1 ? 1152 : 576;
            var length = samples / 8 * bitrate / sampleRate + padding;

            if (length < 24)
            {
                position++;

                continue;
            }

            if (position + length > buffer.Length)
            {
                break;
            }

            media += samples * 1000.0 / sampleRate;
            position += length;
        }

        _pending.RemoveRange(0, Math.Min(position, _pending.Count));

        return media;
    }
}

public sealed class OggClock : IMediaClock
{
    private readonly List<byte> _pending = new();
    private long _lastGranule = -1;

    public double Feed(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data.ToArray());

        var media = 0.0;
        var position = 0;
        var buffer = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);

        while (position + 27 <= buffer.Length)
        {
            if (buffer[position] != (byte)'O' || buffer[position + 1] != (byte)'g' ||
                buffer[position + 2] != (byte)'g' || buffer[position + 3] != (byte)'S')
            {
                position++;

                continue;
            }

            var segments = buffer[position + 26];

            if (position + 27 + segments > buffer.Length)
            {
                break;
            }

            var body = 0;

            for (var index = 0; index < segments; index++)
            {
                body += buffer[position + 27 + index];
            }

            var total = 27 + segments + body;

            if (position + total > buffer.Length)
            {
                break;
            }

            var granule = BitConverter.ToInt64(buffer.Slice(position + 6, 8));

            if (granule > 0)
            {
                if (_lastGranule >= 0 && granule > _lastGranule)
                {
                    media += (granule - _lastGranule) / 48.0;
                }

                _lastGranule = granule;
            }

            position += total;
        }

        _pending.RemoveRange(0, Math.Min(position, _pending.Count));

        return media;
    }
}

public static class Opus
{
    public static int Samples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
        {
            return 0;
        }

        var configuration = packet[0] >> 3;
        int frame;

        if (configuration < 12)
        {
            frame = new[] { 480, 960, 1920, 2880 }[configuration & 0x03];
        }
        else if (configuration < 16)
        {
            frame = (configuration & 0x01) == 0 ? 480 : 960;
        }
        else
        {
            frame = new[] { 120, 240, 480, 960 }[configuration & 0x03];
        }

        var count = (packet[0] & 0x03) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0
        };

        return frame * count;
    }
}

public sealed class OggOpusWriter
{
    private static readonly uint[] Crc = BuildCrc();

    private readonly uint _serial = (uint)Random.Shared.Next();
    private uint _sequence;
    private long _granule;
    private bool _started;

    public byte[] Packet(ReadOnlySpan<byte> opus, int channels)
    {
        var output = new List<byte>();

        if (!_started)
        {
            _started = true;

            var head = new List<byte>();

            head.AddRange("OpusHead"u8.ToArray());
            head.Add(1);
            head.Add((byte)channels);
            head.AddRange(BitConverter.GetBytes((ushort)312));
            head.AddRange(BitConverter.GetBytes(48000u));
            head.AddRange(BitConverter.GetBytes((short)0));
            head.Add(0);

            var tags = new List<byte>();

            tags.AddRange("OpusTags"u8.ToArray());
            tags.AddRange(BitConverter.GetBytes(3u));
            tags.AddRange("lab"u8.ToArray());
            tags.AddRange(BitConverter.GetBytes(0u));

            output.AddRange(Page(head.ToArray(), 0x02, 0));
            output.AddRange(Page(tags.ToArray(), 0x00, 0));
        }

        _granule += Opus.Samples(opus);
        output.AddRange(Page(opus.ToArray(), 0x00, _granule));

        return output.ToArray();
    }

    private byte[] Page(byte[] packet, byte flags, long granule)
    {
        var lacing = new List<byte>();
        var remaining = packet.Length;

        while (remaining >= 255)
        {
            lacing.Add(255);
            remaining -= 255;
        }

        lacing.Add((byte)remaining);

        var page = new List<byte>();

        page.AddRange("OggS"u8.ToArray());
        page.Add(0);
        page.Add(flags);
        page.AddRange(BitConverter.GetBytes(granule));
        page.AddRange(BitConverter.GetBytes(_serial));
        page.AddRange(BitConverter.GetBytes(_sequence++));
        page.AddRange(BitConverter.GetBytes(0u));
        page.Add((byte)lacing.Count);
        page.AddRange(lacing);
        page.AddRange(packet);

        var bytes = page.ToArray();
        var crc = 0u;

        foreach (var value in bytes)
        {
            crc = crc << 8 ^ Crc[(crc >> 24 ^ value) & 0xFF];
        }

        BitConverter.GetBytes(crc).CopyTo(bytes, 22);

        return bytes;
    }

    private static uint[] BuildCrc()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            var value = index << 24;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0 ? value << 1 ^ 0x04C11DB7 : value << 1;
            }

            table[index] = value;
        }

        return table;
    }
}
