using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Blog.Catalog.Infrastructure.Music
{
    internal sealed record Mp3Facts(
        float DurationMinutes, DateOnly? Released, int? TrackNumber, string? Label, IReadOnlyList<string> Genres);

    internal static class Mp3Probe
    {
        private const int MaxTextFrame = 4096;
        private const int SyncSearchWindow = 256 * 1024;

        private static readonly int[] BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        private static readonly int[] BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };

        private static readonly int[][] SampleRates =
        {
            new[] { 11025, 12000, 8000 },
            Array.Empty<int>(),
            new[] { 22050, 24000, 16000 },
            new[] { 44100, 48000, 32000 }
        };

        public static Mp3Facts Read(string path)
        {
            using var stream = File.OpenRead(path);

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var audioStart = ReadId3v2(stream, tags);
            var seconds = Duration(stream, audioStart);

            return new Mp3Facts(
                (float)Math.Round(seconds / 60.0, 2),
                Released(tags),
                TrackNumber(tags.GetValueOrDefault("TRCK")),
                tags.TryGetValue("TPUB", out var label) && label.Length > 0 ? label : null,
                Genres(tags.GetValueOrDefault("TCON")));
        }

        private static long ReadId3v2(Stream stream, Dictionary<string, string> tags)
        {
            Span<byte> header = stackalloc byte[10];

            stream.Position = 0;

            if (stream.Read(header) != 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            {
                return 0;
            }

            var major = header[3];
            var flags = header[5];
            var size = Syncsafe(header[6..10]);
            var end = 10L + size;
            var audioStart = end + ((flags & 0x10) != 0 ? 10 : 0);

            if (major is not (3 or 4) || end > stream.Length)
            {
                return Math.Min(audioStart, stream.Length);
            }

            if ((flags & 0x40) != 0)
            {
                Span<byte> extended = stackalloc byte[4];

                if (stream.Read(extended) != 4)
                {
                    return audioStart;
                }

                stream.Position += major == 4
                    ? Math.Max(0, Syncsafe(extended) - 4)
                    : BinaryPrimitives.ReadInt32BigEndian(extended);
            }

            Span<byte> frame = stackalloc byte[10];

            while (stream.Position + 10 <= end)
            {
                if (stream.Read(frame) != 10 || frame[0] == 0)
                {
                    break;
                }

                var id = Encoding.ASCII.GetString(frame[..4]);
                var length = major == 4 ? Syncsafe(frame[4..8]) : BinaryPrimitives.ReadInt32BigEndian(frame[4..8]);

                if (length <= 0 || stream.Position + length > end)
                {
                    break;
                }

                if (id[0] == 'T' && id != "TXXX" && length <= MaxTextFrame)
                {
                    var data = new byte[length];

                    if (stream.Read(data, 0, length) != length)
                    {
                        break;
                    }

                    tags.TryAdd(id, Text(data));
                }
                else
                {
                    stream.Position += length;
                }
            }

            return audioStart;
        }

        private static double Duration(Stream stream, long audioStart)
        {
            var window = (int)Math.Min(SyncSearchWindow, Math.Max(0, stream.Length - audioStart));

            if (window < 4)
            {
                return 0;
            }

            var buffer = new byte[window];

            stream.Position = audioStart;

            var read = stream.Read(buffer, 0, window);

            for (var i = 0; i + 4 <= read; i++)
            {
                if (buffer[i] != 0xFF || (buffer[i + 1] & 0xE0) != 0xE0)
                {
                    continue;
                }

                var version = (buffer[i + 1] >> 3) & 3;
                var layer = (buffer[i + 1] >> 1) & 3;
                var bitrateIndex = buffer[i + 2] >> 4;
                var sampleRateIndex = (buffer[i + 2] >> 2) & 3;

                if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
                {
                    continue;
                }

                var mpeg1 = version == 3;
                var bitrate = (mpeg1 ? BitratesV1 : BitratesV2)[bitrateIndex] * 1000;
                var sampleRate = SampleRates[version][sampleRateIndex];
                var samplesPerFrame = mpeg1 ? 1152 : 576;
                var mono = (buffer[i + 3] >> 6) == 3;
                var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);

                var frames = FrameCount(buffer, read, i + 4 + sideInfo, "Xing", 8, flagged: true)
                             ?? FrameCount(buffer, read, i + 4 + sideInfo, "Info", 8, flagged: true)
                             ?? FrameCount(buffer, read, i + 4 + 32, "VBRI", 14, flagged: false);

                if (frames is > 0)
                {
                    return frames.Value * (double)samplesPerFrame / sampleRate;
                }

                var audioBytes = stream.Length - audioStart - i - TrailingTag(stream);

                return audioBytes > 0 ? audioBytes * 8.0 / bitrate : 0;
            }

            return 0;
        }

        private static int? FrameCount(byte[] buffer, int read, int offset, string marker, int countOffset, bool flagged)
        {
            if (offset < 0 || offset + countOffset + 4 > read)
            {
                return null;
            }

            for (var k = 0; k < 4; k++)
            {
                if (buffer[offset + k] != marker[k])
                {
                    return null;
                }
            }

            if (flagged && (buffer[offset + 7] & 1) == 0)
            {
                return null;
            }

            return BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset + countOffset, 4));
        }

        private static int TrailingTag(Stream stream)
        {
            if (stream.Length < 128)
            {
                return 0;
            }

            Span<byte> tag = stackalloc byte[3];

            stream.Position = stream.Length - 128;

            return stream.Read(tag) == 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G' ? 128 : 0;
        }

        private static int Syncsafe(ReadOnlySpan<byte> bytes) =>
            ((bytes[0] & 0x7F) << 21) | ((bytes[1] & 0x7F) << 14) | ((bytes[2] & 0x7F) << 7) | (bytes[3] & 0x7F);

        private static string Text(byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            var body = data.AsSpan(1);

            var text = data[0] switch
            {
                1 => DecodeUtf16(body),
                2 => Encoding.BigEndianUnicode.GetString(body),
                3 => Encoding.UTF8.GetString(body),
                _ => Encoding.Latin1.GetString(body)
            };

            return text.Trim('\0', ' ', '﻿');
        }

        private static string DecodeUtf16(ReadOnlySpan<byte> body)
        {
            if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(body[2..]);
            }

            return body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE
                ? Encoding.Unicode.GetString(body[2..])
                : Encoding.Unicode.GetString(body);
        }

        private static DateOnly? Released(Dictionary<string, string> tags)
        {
            foreach (var id in new[] { "TDRC", "TDOR", "TDRL", "TYER" })
            {
                if (!tags.TryGetValue(id, out var value) || value.Length < 4)
                {
                    continue;
                }

                if (value.Length >= 10 &&
                    DateOnly.TryParseExact(
                        value.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    return day;
                }

                if (int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
                    year is >= 1000 and <= 9999)
                {
                    return new DateOnly(year, 1, 1);
                }
            }

            return null;
        }

        private static int? TrackNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var digits = value.Split('/')[0].Trim();

            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number
                : null;
        }

        private static IReadOnlyList<string> Genres(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(new[] { '\0', '/', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => Regex.Replace(g, @"^\(\d+\)", string.Empty).Trim())
                .Where(g => g.Length is > 0 and <= 50 && !g.All(char.IsAsciiDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
        }
    }
}
