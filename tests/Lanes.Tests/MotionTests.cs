using Lanes.Core;
using Xunit;

public class MotionTests
{
    readonly Tuning _t = new();

    [Fact]
    public void PictureFollowsFingersOneScreenPerFullSwipe()
    {
        var s = new SwipeTrack(1536, true, true, _t);
        Assert.Equal(-1536 / 2.0, s.OffsetFor(-_t.FullSwipe / 2), 6);
        Assert.Equal(-1536, s.OffsetFor(-_t.FullSwipe * 3), 6);
    }

    [Fact]
    public void WithoutANeighbourThePictureOnlyStretches()
    {
        var s = new SwipeTrack(1536, false, true, _t);
        double o = s.OffsetFor(_t.FullSwipe);
        Assert.InRange(o, 1, _t.RubberBand * 1536);
        Assert.Equal((Outcome.Stay, 0.0), s.Release(o, 5000));
    }

    [Theory]
    [InlineData(-900, 0, Outcome.Next)]      // past the middle, let go still
    [InlineData(-500, 0, Outcome.Stay)]      // short of the middle
    [InlineData(-200, -2000, Outcome.Next)]  // short, but flicked toward it
    [InlineData(-900, 2000, Outcome.Stay)]   // past the middle, but flicked back
    [InlineData(700, 0, Outcome.Stay)]       // to the previous one, short
    [InlineData(800, 0, Outcome.Previous)]
    public void ReleaseDecidesLikeTheSystemSwipe(double offset, double speed, Outcome expected)
    {
        var s = new SwipeTrack(1536, true, true, _t);
        Assert.Equal(expected, s.Release(offset, speed).Outcome);
    }

    [Fact]
    public void SpringStartsWhereTheFingersLeftItAndSettles()
    {
        var sp = new Spring(-900, -3000, -1536, _t.SwitchResponse);
        Assert.Equal(-900, sp.At(0), 6);
        Assert.InRange(sp.VelocityAt(0), -3010, -2990);
        Assert.True(sp.Done(0.6));
        Assert.False(sp.Done(0.05));
        // Critically damped: never past the target.
        for (double t = 0; t < 1; t += 0.004) Assert.True(sp.At(t) >= -1536 - 1e-6);
    }

    [Fact]
    public void OverviewOpensUpAndClosesDown()
    {
        var open = new OverviewTrack(0, _t);
        Assert.Equal(0.5, open.ProgressFor(-_t.FullOverview / 2), 6);
        Assert.InRange(open.ProgressFor(-_t.FullOverview * 3), 1, 1.06);
        Assert.True(open.Release(0.6, 0));
        Assert.True(open.Release(0.2, open.SpeedFor(-1)));
        var close = new OverviewTrack(1, _t);
        Assert.Equal(0.5, close.ProgressFor(_t.FullOverview / 2), 6);
        Assert.False(close.Release(0.45, 0));
    }
}
