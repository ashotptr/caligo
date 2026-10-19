using System.Text;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Media;
using Caligo.Catalog.Domain.Media;
using Xunit;

namespace Caligo.Catalog.UnitTests;

public class MediaRulesTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-media-signing-key-0123456789");
    private static readonly DateTimeOffset Expiry = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AToken_CarriesItsSessionExpiryAndPackage()
    {
        var session = Guid.NewGuid();
        var token = MediaToken.Create(Key, session, Expiry, "12/abc");

        Assert.Equal(MediaToken.Length, token.Length);
        Assert.True(MediaPaths.IsSafeRelative(token));
        Assert.True(MediaToken.TryRead(Key, token, out var grant));
        Assert.Equal(session, grant.SessionId);
        Assert.Equal(Expiry, grant.ExpiresAt);
        Assert.Equal(MediaToken.ScopeOf("12/abc"), grant.Scope);
        Assert.NotEqual(MediaToken.ScopeOf("12/abd"), grant.Scope);
    }

    [Fact]
    public void AToken_IsTheSameForTheSameSession_SoARepeatedStartGivesTheSameUrl()
    {
        var session = Guid.NewGuid();

        Assert.Equal(
            MediaToken.Create(Key, session, Expiry, "12/abc"),
            MediaToken.Create(Key, session, Expiry, "12/abc"));
    }

    [Fact]
    public void ChangingAnyCharacter_BreaksTheToken()
    {
        var token = MediaToken.Create(Key, Guid.NewGuid(), Expiry, "12/abc");

        for (var i = 0; i < token.Length; i++)
        {
            var changed = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];

            Assert.False(MediaToken.TryRead(Key, changed, out _), $"position {i} was not protected");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void SomethingThatIsNotAToken_IsRefused(string? token)
    {
        Assert.False(MediaToken.TryRead(Key, token, out _));
    }

    [Fact]
    public void TextOfTheRightLength_IsStillNotAToken()
    {
        var unsigned = new string('A', MediaToken.Length);
        var padded = new string('A', MediaToken.Length - 2) + "==";
        var traversal = string.Concat(Enumerable.Repeat("../", MediaToken.Length / 3)) + "/";

        Assert.Equal(MediaToken.Length, traversal.Length);
        Assert.False(MediaToken.TryRead(Key, unsigned, out _));
        Assert.False(MediaToken.TryRead(Key, padded, out _));
        Assert.False(MediaToken.TryRead(Key, traversal, out _));
    }

    [Fact]
    public void ATokenSignedWithAnotherKey_IsRefused()
    {
        var token = MediaToken.Create(Encoding.UTF8.GetBytes("another-key-another-key-another-key!"), Guid.NewGuid(), Expiry, "12/abc");

        Assert.False(MediaToken.TryRead(Key, token, out _));
    }

    [Theory]
    [InlineData("master.m3u8", true)]
    [InlineData("subs/2.vtt", true)]
    [InlineData("seg-0-00001.m4s", true)]
    [InlineData("a/b/c/d.mp4", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/master.m3u8", false)]
    [InlineData("subs//2.vtt", false)]
    [InlineData("subs/", false)]
    [InlineData("../secret.mp4", false)]
    [InlineData("subs/../../secret.mp4", false)]
    [InlineData("subs/..%2f..%2fsecret.mp4", false)]
    [InlineData(".hidden/x.mp4", false)]
    [InlineData("subs\\2.vtt", false)]
    [InlineData("C:/x.mp4", false)]
    [InlineData("seg 1.m4s", false)]
    [InlineData("seg\n1.m4s", false)]
    public void OnlyPlainRelativePaths_AreServed(string? path, bool safe)
    {
        Assert.Equal(safe, MediaPaths.IsSafeRelative(path));
    }

    [Fact]
    public void AVeryLongPath_IsRefused()
    {
        Assert.False(MediaPaths.IsSafeRelative(new string('a', MediaPaths.MaxLength + 1)));
    }

    [Theory]
    [InlineData("master.m3u8", "application/vnd.apple.mpegurl")]
    [InlineData("manifest.mpd", "application/dash+xml")]
    [InlineData("seg-0-00001.m4s", "video/iso.segment")]
    [InlineData("progressive.MP4", "video/mp4")]
    [InlineData("subs/1.vtt", "text/vtt; charset=utf-8")]
    [InlineData("notes.txt", null)]
    [InlineData("master", null)]
    [InlineData("job.json", null)]
    public void OnlyMediaFiles_HaveAContentType(string path, string? expected)
    {
        Assert.Equal(expected, MediaPaths.ContentType(path));
    }

    [Theory]
    [InlineData("Film.mp4", true)]
    [InlineData("Film (1999).MKV", true)]
    [InlineData("Film.vtt", false)]
    [InlineData("Film", false)]
    [InlineData("../Film.mp4", false)]
    [InlineData("folder/Film.mp4", false)]
    [InlineData("folder\\Film.mp4", false)]
    [InlineData("C:Film.mp4", false)]
    [InlineData(".Film.mp4", false)]
    [InlineData(" Film.mp4", false)]
    [InlineData(null, false)]
    public void ASource_IsAPlainVideoFileName(string? name, bool expected)
    {
        Assert.Equal(expected, MediaPaths.IsSourceFile(name));
    }

    [Fact]
    public void Sidecars_AreSubtitleFilesNamedAfterTheSourceAndALanguage()
    {
        var files = new[]
        {
            "Fight Club.mp4", "Fight Club.English.vtt", "Fight Club.spanish.srt", "Fight Club.vtt",
            "Fight Club 2.English.vtt", "Fight Club.English.forced.vtt", "Fight Club.English.txt", "Other.English.vtt"
        };

        var sidecars = Sidecars.Of("Fight Club.mp4", files);

        Assert.Equal(new[] { "English", "spanish" }, sidecars.Select(s => s.Tag));
        Assert.Equal(new[] { "Fight Club.English.vtt", "Fight Club.spanish.srt" }, sidecars.Select(s => s.File));
    }

    [Theory]
    [InlineData("{\"devices_household_can_watch_at_same_time\": 2}", 2)]
    [InlineData("{\"devices_household_can_watch_at_same_time\": \"4\"}", 4)]
    [InlineData("{\"devices_household_can_watch_at_same_time\": 500}", PlanLimits.MaxStreamsAllowed)]
    [InlineData("{\"devices_household_can_watch_at_same_time\": 0}", null)]
    [InlineData("{\"devices_household_can_watch_at_same_time\": 1.5}", null)]
    [InlineData("{\"devices_household_can_watch_at_same_time\": \"many\"}", null)]
    [InlineData("{\"name\": \"Basic\"}", null)]
    [InlineData("[1, 2]", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheStreamLimit_IsReadFromThePlanText(string? planInfo, int? expected)
    {
        Assert.Equal(expected, PlanLimits.MaxStreams(planInfo));
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData(" spanish ", "es")]
    [InlineData("Armenian", "hy")]
    [InlineData("Klingon", null)]
    [InlineData(null, null)]
    public void ALanguageName_MapsToItsTag(string? name, string? expected)
    {
        Assert.Equal(expected, LanguageTags.For(name));
    }

    [Theory]
    [InlineData("abcd1234", true)]
    [InlineData("3f2a9c1e-7b1d-4a0e-9b7a-1c2d3e4f5a6b", true)]
    [InlineData("device_01-A", true)]
    [InlineData("short", false)]
    [InlineData("has space 1234", false)]
    [InlineData("semi;colon1234", false)]
    [InlineData(null, false)]
    public void ADeviceId_IsAShortPlainToken(string? deviceId, bool valid)
    {
        Assert.Equal(valid, PlaybackSession.IsValidDeviceId(deviceId));
        Assert.False(PlaybackSession.IsValidDeviceId(new string('a', PlaybackSession.MaxDeviceIdLength + 1)));
    }

    [Fact]
    public void ASession_IsLiveUntilItsLeaseRunsOutOrItIsEnded()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var session = new PlaybackSession { ExpiresAt = now.AddSeconds(60) };

        Assert.True(session.IsLiveAt(now));
        Assert.False(session.IsLiveAt(now.AddSeconds(60)));

        session.EndedAt = now;

        Assert.False(session.IsLiveAt(now));
    }
}
