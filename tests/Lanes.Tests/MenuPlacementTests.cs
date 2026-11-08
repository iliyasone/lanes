using Lanes.Core;
using Xunit;

public class MenuPlacementTests
{
    static readonly Rect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void OpensAboveATaskbarAtTheBottom()
    {
        var (x, y) = MenuPlacement.Place(Screen, new Rect(0, 0, 1920, 1032), 300, 400, 1800, 1050, 12);
        Assert.Equal(1032 - 400 - 12, y);
        Assert.Equal(1920 - 300 - 12, x);
    }

    [Fact]
    public void OpensBelowATaskbarAtTheTop()
    {
        var (x, y) = MenuPlacement.Place(Screen, new Rect(0, 40, 1920, 1080), 300, 400, 1700, 20, 12);
        Assert.Equal(40 + 12, y);
        Assert.Equal(1700 - 150, x);
    }

    [Fact]
    public void OpensBesideATaskbarOnTheLeftOrRight()
    {
        Assert.Equal((60, 1080 - 400 - 12), MenuPlacement.Place(Screen, new Rect(48, 0, 1920, 1080), 300, 400, 20, 1060, 12));
        Assert.Equal((1872 - 300 - 12, 300), MenuPlacement.Place(Screen, new Rect(0, 0, 1872, 1080), 300, 400, 1900, 500, 12));
    }
}
