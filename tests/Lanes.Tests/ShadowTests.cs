using Lanes.Core;
using Xunit;

public class ShadowTests
{
    [Fact]
    public void TheShadowIsHalfAtAnEdgeFullWellInsideAndGoneBeyondItsMargin()
    {
        var shape = new ShadowShape(8, 16);
        Assert.InRange(shape.At(400, 300, 800, 600), 0.99f, 1.01f);
        Assert.InRange(shape.At(-0.0, 300, 800, 600), 0.45f, 0.55f);
        Assert.Equal(0, shape.At(-shape.Margin - 1, 300, 800, 600));
        Assert.Equal(0, shape.At(400, 600 + shape.Margin + 1, 800, 600));
    }

    [Fact]
    public void TheShadowFadesOutwardTheSameOnEverySide()
    {
        var shape = new ShadowShape(8, 16);
        float last = 1;
        for (int d = 0; d < shape.Margin; d += 4)
        {
            float left = shape.At(-d, 300, 800, 600), right = shape.At(800 + d, 300, 800, 600);
            float top = shape.At(400, -d, 800, 600), bottom = shape.At(400, 600 + d, 800, 600);
            Assert.True(left <= last + 1e-4, $"{d}: {left} after {last}");
            Assert.InRange(right - left, -0.03f, 0.03f);
            Assert.InRange(top - left, -0.03f, 0.03f);
            Assert.InRange(bottom - left, -0.03f, 0.03f);
            last = left;
        }
    }

    [Fact]
    public void AWindowsShadowFallsBelowItAndNotOnTheWindow()
    {
        var look = new ShadowLook(1);
        Assert.Equal(0, look.At(400, 300, 800, 600));
        float above = look.At(400, -10, 800, 600), below = look.At(400, 610, 800, 600);
        Assert.True(below > above * 2, $"{below} below, {above} above");
        Assert.True(look.Bottom > look.Top);
        // At a rounded corner, outside the window's rounding, there is shadow.
        Assert.True(look.At(0.5, 599.5, 800, 600) > 0.1f);
        Assert.True(look.At(0.5, 0.5, 800, 600) > 0.02f);
    }

    [Fact]
    public void ThePiecesHoldTheWholeShadowAroundTheWindow()
    {
        var look = new ShadowLook(1.25);
        var frame = new PixelRect(100, 80, 640, 400);
        var pieces = look.Pieces(frame);
        bool In(int x, int y) => pieces.Any(p => x >= p.X && x < p.Right && y >= p.Y && y < p.Bottom);
        for (int y = frame.Y - look.Top - 4; y < frame.Bottom + look.Bottom + 4; y += 3)
            for (int x = frame.X - look.Side - 4; x < frame.Right + look.Side + 4; x += 3)
                if (look.At(x + 0.5 - frame.X, y + 0.5 - frame.Y, frame.W, frame.H) > 0.002f)
                    Assert.True(In(x, y), $"shadow at {x},{y} is in no piece");
        // They stay off the middle of the window.
        Assert.False(In(frame.X + frame.W / 2, frame.Y + frame.H / 2));
    }

    [Fact]
    public void DrawingAPieceGivesTheSameShadowAsAskingPixelByPixel()
    {
        var look = new ShadowLook(1);
        var frame = new PixelRect(50, 40, 300, 200);
        var piece = look.Pieces(frame)[1];
        var pixels = new uint[piece.W * piece.H];
        look.Draw(pixels, piece.W, piece, frame);
        for (int j = 0; j < piece.H; j += 5)
            for (int i = 0; i < piece.W; i += 7)
            {
                float want = look.At(piece.X + i + 0.5 - frame.X, piece.Y + j + 0.5 - frame.Y, frame.W, frame.H);
                Assert.InRange((int)(pixels[j * piece.W + i] >> 24) - want * 255, -1, 1);
            }
    }
}
