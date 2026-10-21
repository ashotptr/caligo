using Caligo.Catalog.Infrastructure.Engine;
using Xunit;

namespace Caligo.Api.Tests;

public class MixtapePathsTests
{
    [Theory]
    [InlineData("first song.mp3", "first%20song.mp3")]
    [InlineData("why'd-you-only-call-me-when-you're-high.mp3", "why'd-you-only-call-me-when-you're-high.mp3")]
    [InlineData("tranquility-base-hotel-&-casino", "tranquility-base-hotel-&-casino")]
    [InlineData("exit-music-(for-a-film).mp3", "exit-music-(for-a-film).mp3")]
    [InlineData("what?#\"<>`{}.mp3", "what%3F%23%22%3C%3E%60%7B%7D.mp3")]
    [InlineData("déjà.mp3", "d%C3%A9j%C3%A0.mp3")]
    public void Names_AreWrittenTheWayABrowserRequestsThem(string name, string expected) =>
        Assert.Equal(expected, MixtapePaths.Segment(name));

    [Fact]
    public void Split_ReadsBackWhatSegmentWrote()
    {
        var uri = "/mixtape/" + MixtapePaths.Segment("an album") + "/" + MixtapePaths.Segment("first song's.mp3");

        Assert.Equal(("an album", "first song's.mp3"), MixtapePaths.Split(uri, "/mixtape/"));
    }
}
