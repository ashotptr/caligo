using System.Security.Cryptography;
using System.Text;

namespace Caligo.Catalog.Application.Media;

public class MediaRules
{
    public const string SectionName = "Media";
    public const int MinSigningKeyBytes = 32;
    public const int MaxEventsPerBatch = 100;

    public bool Enabled { get; set; }

    public string SigningKey { get; set; } = string.Empty;

    public string PublicPrefix { get; set; } = "/vod/";

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan MaxSessionAge { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan SessionRetention { get; set; } = TimeSpan.FromDays(30);

    public int DefaultMaxStreams { get; set; } = 1;

    public TimeSpan PackagingClaim { get; set; } = TimeSpan.FromMinutes(2);

    public int MaxPackagingAttempts { get; set; } = 3;
}

public static class InternalKeys
{
    public const string Header = "X-Caligo-Key";

    private const string Purpose = "caligo-internal-v1";

    public static string Derive(string signingKey) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(Purpose))).ToLowerInvariant();

    public static bool Matches(string signingKey, string? presented)
    {
        if (string.IsNullOrEmpty(signingKey) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Derive(signingKey));
        var given = Encoding.ASCII.GetBytes(presented);

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
