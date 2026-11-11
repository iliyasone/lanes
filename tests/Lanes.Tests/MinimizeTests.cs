using Lanes.Core;
using Xunit;

public class MinimizeTests
{
    static readonly Box Window = new(100, 100, 800, 600), Button = Minimize.Into(Window, 500, 1050, 40);

    [Fact]
    public void ItStartsAsTheWindowAndEndsInTheButtonFaded()
    {
        var (start, a0) = Minimize.At(Window, Button, 0);
        Assert.Equal(Window, start);
        Assert.Equal(1, a0);
        var (end, a1) = Minimize.At(Window, Button, 1);
        Assert.Equal(Button, end);
        Assert.Equal(0, a1);
    }

    [Fact]
    public void ItShrinksBeforeItTravelsAndFadesOnlyAtTheEnd()
    {
        var (b, a) = Minimize.At(Window, Button, 0.3);
        double travelled = (b.Y + b.H / 2 - 400) / (1050 - 400), shrunk = (800 - b.W) / (800 - 40);
        Assert.True(shrunk > travelled, $"shrunk {shrunk:0.00}, travelled {travelled:0.00}");
        Assert.Equal(1, a);
        Assert.True(Minimize.At(Window, Button, 0.85).Alpha < 1);
    }

    [Fact]
    public void TheButtonBoxKeepsTheWindowsShape()
    {
        Assert.Equal(40, Button.W);
        Assert.Equal(30, Button.H, 6);
        Assert.Equal(500, Button.X + Button.W / 2, 6);
        Assert.Equal(1050, Button.Y + Button.H / 2, 6);
    }
}
