using Xunit;

namespace Blog.StreamLab.Tests;

public class PlaybackModelTests
{
    [Fact]
    public void PlaybackStartsOnceHalfASecondIsBuffered()
    {
        var model = new PlaybackModel();

        model.Arrive(100, 300);

        Assert.Null(model.StartedAt);

        model.Arrive(250, 300);

        Assert.Equal(250, model.StartedAt);
    }

    [Fact]
    public void RealTimeArrivalsNeverStall()
    {
        var model = new PlaybackModel();

        model.Arrive(0, 1000);

        for (var now = 20; now <= 30000; now += 20)
        {
            model.Arrive(now, 20);
        }

        model.Finish(30000);

        Assert.Equal(0, model.Stalls);
        Assert.Equal(0, model.StallMs);
    }

    [Fact]
    public void AGapLongerThanTheBufferStallsUntilHalfASecondIsBack()
    {
        var model = new PlaybackModel();

        model.Arrive(0, 1000);
        model.Arrive(3000, 200);

        Assert.Equal(1, model.Stalls);

        model.Arrive(3400, 400);

        Assert.Equal(2400, model.StallMs, 3);
        Assert.Equal(1, model.Stalls);
    }

    [Fact]
    public void PlayedAtShiftsLaterMediaByTheStallsBeforeIt()
    {
        var model = new PlaybackModel();

        model.Arrive(0, 1000);
        model.Arrive(3000, 600);
        model.Finish(3000);

        Assert.Equal(500, model.PlayedAt(500));
        Assert.Equal(1300 + 2000, model.PlayedAt(1300));
    }

    [Fact]
    public void AStallThatNeverRecoversCountsUntilTheEnd()
    {
        var model = new PlaybackModel();

        model.Arrive(0, 600);
        model.Finish(2000);

        Assert.Equal(1, model.Stalls);
        Assert.Equal(1400, model.StallMs, 3);
    }
}
