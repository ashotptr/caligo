using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Caligo.Catalog.Application.Media;

public readonly record struct MediaGrant(Guid SessionId, DateTimeOffset ExpiresAt, ulong Scope);

public static class MediaToken
{
    private const int SessionLength = 16;
    private const int PayloadLength = SessionLength + 8 + 8;
    private const int MacLength = 16;

    public const int Length = (PayloadLength + MacLength) * 4 / 3;

    public static string Create(byte[] key, Guid sessionId, DateTimeOffset expiresAt, string storagePath)
    {
        Span<byte> token = stackalloc byte[PayloadLength + MacLength];

        sessionId.TryWriteBytes(token[..SessionLength], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(token.Slice(SessionLength, 8), expiresAt.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64BigEndian(token.Slice(SessionLength + 8, 8), ScopeOf(storagePath));

        Sign(key, token[..PayloadLength], token[PayloadLength..]);

        return Encode(token);
    }

    public static bool TryRead(byte[] key, string? token, out MediaGrant grant)
    {
        grant = default;

        if (token is null || token.Length != Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[PayloadLength + MacLength];

        if (!TryDecode(token, bytes))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[MacLength];

        Sign(key, bytes[..PayloadLength], expected);

        if (!CryptographicOperations.FixedTimeEquals(expected, bytes[PayloadLength..]))
        {
            return false;
        }

        var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(SessionLength, 8));

        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        grant = new MediaGrant(
            new Guid(bytes[..SessionLength], bigEndian: true),
            DateTimeOffset.FromUnixTimeSeconds(seconds),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(SessionLength + 8, 8)));

        return true;
    }

    public static ulong ScopeOf(string storagePath)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(storagePath), digest);

        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    private static void Sign(byte[] key, ReadOnlySpan<byte> payload, Span<byte> mac)
    {
        Span<byte> full = stackalloc byte[HMACSHA256.HashSizeInBytes];

        HMACSHA256.HashData(key, payload, full);

        full[..MacLength].CopyTo(mac);
    }

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string token, Span<byte> bytes)
    {
        Span<char> standard = stackalloc char[token.Length];

        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];

            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }

            standard[i] = c == '-' ? '+' : c == '_' ? '/' : c;
        }

        return Convert.TryFromBase64Chars(standard, bytes, out var written) && written == bytes.Length;
    }
}
