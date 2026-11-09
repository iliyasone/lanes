using Lanes.Core;
using Xunit;

public class SpreadTests
{
    static bool Overlap(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    static void Inside(Box[] r, Box area)
    {
        foreach (var b in r)
        {
            Assert.True(b.X >= area.X - 0.01 && b.Right <= area.Right + 0.01, $"{b}");
            Assert.True(b.Y >= area.Y - 0.01 && b.Bottom <= area.Bottom + 0.01, $"{b}");
        }
    }

    static void Apart(Box[] r)
    {
        for (int i = 0; i < r.Length; i++)
            for (int j = i + 1; j < r.Length; j++)
                Assert.False(Overlap(r[i], r[j]), $"{r[i]} {r[j]}");
    }

    [Fact]
    public void FitsTheAreaKeepsSizeOrderAndDoesNotOverlap()
    {
        var wins = new[] { new Box(60, 30, 1010, 640), new Box(900, 430, 560, 320), new Box(1110, 70, 360, 300) };
        var area = new Box(72, 470, 1392, 314);
        var r = Spread.Layout(wins, area);
        Inside(r, area);
        Apart(r);
        Assert.True(r[0].W > r[1].W && r[1].W > r[2].W);
        Assert.Equal(1010.0 / 640, r[0].W / r[0].H, 3);
    }

    [Fact]
    public void AWindowThatFitsKeepsItsSize()
    {
        var area = new Box(0, 300, 1400, 700);
        var r = Spread.Layout(new[] { new Box(500, 100, 600, 400) }, area);
        Assert.Equal(600, r[0].W, 3);
        Assert.Equal(400, r[0].H, 3);
        Inside(r, area);
    }

    [Fact]
    public void WindowsAlreadyApartKeepTheirPlacesAndSize()
    {
        var wins = new[] { new Box(100, 200, 400, 300), new Box(900, 120, 400, 300) };
        var r = Spread.Layout(wins, new Box(0, 0, 1600, 900));
        Assert.Equal(400, r[0].W, 3);
        Assert.Equal(400, r[1].W, 3);
        // Same distance apart as on the screen.
        Assert.Equal(800, r[1].X - r[0].X, 3);
        Assert.Equal(-80, r[1].Y - r[0].Y, 3);
    }

    [Fact]
    public void TwoMaximizedWindowsGoSideBySide()
    {
        var max = new Box(0, 40, 1920, 1040);
        var area = new Box(90, 430, 1740, 600);
        var r = Spread.Layout(new[] { max, max }, area);
        Inside(r, area);
        Apart(r);
        Assert.Equal(r[0].Y, r[1].Y, 3);
        Assert.Equal(r[0].W, r[1].W, 3);
        Assert.True(r[0].W > 700, $"{r[0]}");
    }

    [Fact]
    public void ManyOverlappingWindowsEndApartInTheArea()
    {
        var wins = Enumerable.Range(0, 9).Select(i => new Box(i * 50, i * 40, 800, 600)).ToArray();
        var area = new Box(0, 0, 1400, 700);
        var r = Spread.Layout(wins, area);
        Inside(r, area);
        Apart(r);
    }
}
