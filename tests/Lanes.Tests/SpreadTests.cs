using Lanes.Core;
using Xunit;

public class SpreadTests
{
    [Fact]
    public void FitsTheAreaKeepsSizeOrderAndDoesNotOverlap()
    {
        var wins = new[] { new Box(60, 30, 1010, 640), new Box(900, 430, 560, 320), new Box(1110, 70, 360, 300) };
        var area = new Box(72, 470, 1392, 314);
        var r = Spread.Layout(wins, area);
        foreach (var b in r)
        {
            Assert.True(b.X >= area.X - 0.01 && b.Right <= area.Right + 0.01, $"{b}");
            Assert.True(b.Y >= area.Y - 0.01 && b.Bottom <= area.Bottom + 0.01, $"{b}");
        }
        Assert.True(r[0].W > r[1].W && r[1].W > r[2].W);
        for (int i = 0; i < r.Length; i++)
            for (int j = i + 1; j < r.Length; j++)
                Assert.False(r[i].X < r[j].Right && r[j].X < r[i].Right && r[i].Y < r[j].Bottom && r[j].Y < r[i].Bottom);
        // Aspect ratio kept.
        Assert.Equal(1010.0 / 640, r[0].W / r[0].H, 3);
    }

    [Fact]
    public void ManyWindowsWrapIntoRows()
    {
        var wins = Enumerable.Range(0, 9).Select(i => new Box(i * 50, i * 40, 800, 600)).ToArray();
        var r = Spread.Layout(wins, new Box(0, 0, 1400, 700));
        Assert.True(r.Select(b => b.Y).Distinct().Count() >= 2);
    }
}
