using Lanes.Core;
using Xunit;

public class GestureTests
{
    sealed class Sink : IGestureSink
    {
        public List<string> Log = new();
        public (double X, double Y) LastMove;
        public (double X, double Y) EndVelocity;
        public void Begin(GestureKind kind, Axis axis) => Log.Add($"begin {kind} {axis}");
        public void Move(double dx, double dy) => LastMove = (dx, dy);
        public void End(double vx, double vy) { EndVelocity = (vx, vy); Log.Add("end"); }
    }

    // Fingers spread across the pad, all moving by (dx, dy) per report every 8 ms.
    static IEnumerable<TouchFrame> Swipe(int fingers, double dx, double dy, int steps, long start = 0, uint firstId = 1)
    {
        for (int s = 0; s <= steps; s++)
            yield return new TouchFrame(start + s * 8,
                Enumerable.Range(0, fingers).Select(f => new Finger((uint)(firstId + f), 0.3 + 0.1 * f + dx * s, 0.3 + dy * s)).ToArray());
    }

    static TouchFrame Lift(long t) => new(t, Array.Empty<Finger>());

    [Fact]
    public void FourFingersSidewaysIsAHorizontalSwitch()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, -0.01, 0.001, 30)) g.OnFrame(f);
        g.OnFrame(Lift(30 * 8 + 8));
        Assert.Equal(new[] { "begin Switch Horizontal", "end" }, sink.Log);
        Assert.True(sink.LastMove.X < -0.25, $"travel {sink.LastMove.X}");
        Assert.InRange(sink.EndVelocity.X, -1.3, -1.2); // 0.01 pad widths per 8 ms
    }

    [Fact]
    public void FourFingersDownIsAVerticalSwitch()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, 0.002, 0.01, 20)) g.OnFrame(f);
        Assert.Equal("begin Switch Vertical", sink.Log[0]);
        Assert.True(sink.LastMove.Y > 0.15);
    }

    [Fact]
    public void ThreeFingersUpIsTheOverviewAndSidewaysIsLeftToWindows()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(3, 0, -0.01, 10)) g.OnFrame(f);
        g.OnFrame(Lift(100));
        Assert.Equal("begin Overview Vertical", sink.Log[0]);

        var side = new Sink();
        g = new GestureRecognizer(side);
        foreach (var f in Swipe(3, 0.01, 0, 10)) g.OnFrame(f);
        g.OnFrame(Lift(100));
        Assert.Empty(side.Log);
    }

    [Fact]
    public void SmallJitterIsNotASwipe()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, 0.001, 0.001, 10)) g.OnFrame(f);
        g.OnFrame(Lift(100));
        Assert.Empty(sink.Log);
    }

    [Fact]
    public void AFingerLandingLateDoesNotMakeThePictureJump()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        // Three fingers first, then a fourth far to the right; then all four move left together.
        // The fourth lands at x 0.6, which would pull the fingers' centre right by 0.075.
        g.OnFrame(new TouchFrame(0, new[] { new Finger(1, .3, .3), new Finger(2, .4, .3), new Finger(3, .5, .3) }));
        foreach (var f in Swipe(4, -0.01, 0, 20, 8)) g.OnFrame(f);
        Assert.Equal("begin Switch Horizontal", sink.Log[0]);
        // 20 steps of 0.01 minus the distance needed to recognise the swipe, with no jump.
        Assert.InRange(sink.LastMove.X, -0.18, -0.165);
    }

    [Fact]
    public void AFingerLiftingEarlyKeepsTheSwipeGoing()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, -0.01, 0, 10)) g.OnFrame(f);
        foreach (var f in Swipe(3, -0.01, 0, 10, 88)) g.OnFrame(f);
        Assert.Single(sink.Log);
        g.OnFrame(Lift(200));
        Assert.Equal("end", sink.Log[^1]);
    }

    [Fact]
    public void HoldingStillBeforeLiftingMeansNoFlick()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, -0.01, 0, 20)) g.OnFrame(f);
        var still = Swipe(4, -0.01, 0, 20).Last().Fingers;
        for (int i = 1; i <= 15; i++) g.OnFrame(new TouchFrame(160 + i * 8, still));
        g.OnFrame(Lift(300));
        Assert.InRange(sink.EndVelocity.X, -0.01, 0.01);
    }

    [Fact]
    public void ReportsStoppingEndTheSwipe()
    {
        var sink = new Sink();
        var g = new GestureRecognizer(sink);
        foreach (var f in Swipe(4, -0.01, 0, 10)) g.OnFrame(f);
        g.OnTick(80 + 100);
        Assert.Single(sink.Log);
        g.OnTick(80 + GestureRecognizer.SilenceMs + 1);
        Assert.Equal("end", sink.Log[^1]);
    }
}
