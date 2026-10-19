namespace Caligo.StreamLab.Media;

public static class Varint
{
    public static int Size(ulong value) => value < 0x40 ? 1 : value < 0x4000 ? 2 : value < 0x40000000 ? 4 : 8;

    public static void Write(List<byte> output, ulong value)
    {
        var size = Size(value);
        var prefix = size switch { 1 => 0UL, 2 => 1UL, 4 => 2UL, _ => 3UL };
        var encoded = value | prefix << (size * 8 - 2);

        for (var index = size - 1; index >= 0; index--)
        {
            output.Add((byte)(encoded >> (index * 8)));
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;

        if (position >= data.Length)
        {
            return false;
        }

        var size = 1 << (data[position] >> 6);

        if (position + size > data.Length)
        {
            return false;
        }

        value = (ulong)(data[position] & 0x3F);

        for (var index = 1; index < size; index++)
        {
            value = value << 8 | data[position + index];
        }

        position += size;

        return true;
    }

    public static byte[] Message(params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToList();
        var output = new List<byte>();

        Write(output, (ulong)body.Count);
        output.AddRange(body);

        return output.ToArray();
    }

    public static byte[] Of(ulong value)
    {
        var output = new List<byte>();

        Write(output, value);

        return output.ToArray();
    }

    public static byte[] Text(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var output = new List<byte>();

        Write(output, (ulong)bytes.Length);
        output.AddRange(bytes);

        return output.ToArray();
    }
}
