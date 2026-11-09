namespace Lanes.Core;

// The shadow of a rounded rectangle, as light spreads round it: a rounded square with corners of
// radius `radius`, blurred once (a Gaussian of width `sigma`), from which the shadow of a
// rectangle of any size is cut: the square's corners as they are, its middle stretched along
// the sides.
public sealed class ShadowShape
{
    public int Radius { get; }
    public int Sigma { get; }
    // How far past the edges the shadow reaches (3 sigma), the side of the blurred square with
    // that round it, and how far from its outer edge each corner piece reaches.
    public int Margin { get; }
    public int Size { get; }
    public int Corner { get; }
    // How dark the shadow is in each pixel of the blurred square, 0..1, row by row.
    public float[] Alpha { get; }

    public ShadowShape(int radius, int sigma)
    {
        Radius = Math.Max(1, radius);
        Sigma = Math.Max(1, sigma);
        int m = 3 * Sigma, c = 2 * Radius + 2 * m + 4, t = c + 2 * m;
        Margin = m;
        Size = t;
        Corner = 2 * m + Radius;
        var a = new float[t * t];
        for (int y = 0; y < c; y++)
            for (int x = 0; x < c; x++)
                a[(y + m) * t + x + m] = (float)Coverage(x + 0.5, y + 0.5, c, c, Radius);
        // Three box blurs one after another come within a hair of a Gaussian.
        var tmp = new float[t * t];
        foreach (int box in Boxes(Sigma))
        {
            Blur(a, tmp, t, (box - 1) / 2, true);
            Blur(tmp, a, t, (box - 1) / 2, false);
        }
        Alpha = a;
    }

    // How much of the pixel centred on (x, y) a w x h rectangle at the origin with corners of
    // radius r covers, 0..1.
    public static double Coverage(double x, double y, double w, double h, double r)
    {
        double dx = Math.Max(0, Math.Max(r - x, x - (w - r))), dy = Math.Max(0, Math.Max(r - y, y - (h - r)));
        double round = r + 0.5 - Math.Sqrt(dx * dx + dy * dy), edge = Math.Min(Math.Min(x, w - x), Math.Min(y, h - y)) + 0.5;
        return Math.Clamp(Math.Min(round, edge), 0, 1);
    }

    // Which column (or row) of the blurred square a point `u` along a side of length `len` takes
    // its darkness from (u from the side's start; the shadow begins `Margin` before it): within a
    // corner piece as it is (the pieces squeezed when the side is too short for both), anywhere
    // else the middle. -1 outside the shadow.
    public int Index(double u, double len)
    {
        double total = len + 2 * Margin, p = u + Margin;
        if (p < 0 || p >= total) return -1;
        double k = Math.Min(Corner, total / 2);
        double s = p < k ? p * Corner / k : p >= total - k ? Size - (total - p) * Corner / k : Size / 2.0;
        return Math.Clamp((int)s, 0, Size - 1);
    }

    // How dark the shadow of a w x h rectangle is at (x, y), measured from its top-left corner.
    public float At(double x, double y, double w, double h)
    {
        int i = Index(x, w), j = Index(y, h);
        return i < 0 || j < 0 ? 0 : Alpha[j * Size + i];
    }

    static int[] Boxes(double sigma)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int mm = (int)Math.Round((12 * sigma * sigma - 3 * wl * wl - 12 * wl - 9) / (-4.0 * wl - 4));
        return new[] { 0, 1, 2 }.Select(i => i < mm ? wl : wu).ToArray();
    }

    // A running average over 2r+1 pixels along rows (or columns) of a t x t square; outside it is empty.
    static void Blur(float[] src, float[] dst, int t, int r, bool rows)
    {
        float k = 1f / (2 * r + 1);
        for (int line = 0; line < t; line++)
        {
            float sum = 0;
            int At(int i) => rows ? line * t + i : i * t + line;
            for (int i = 0; i < Math.Min(r, t); i++) sum += src[At(i)];
            for (int i = 0; i < t; i++)
            {
                if (i + r < t) sum += src[At(i + r)];
                dst[At(i)] = sum * k;
                if (i - r >= 0) sum -= src[At(i - r)];
            }
        }
    }
}

// A pixel rectangle on the screen.
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

// A window's shadow as on a Mac, the same on the desktop and in the overview: a soft one well
// below the window and a short dark one tight round its edges, under corners as round as
// Windows 11 makes them. Sizes are at 96 dpi; `scale` is the screen's.
public sealed class ShadowLook
{
    public const double Corner = 8, SoftSigma = 16, SoftDrop = 14, SoftAlpha = 0.42, TightSigma = 3, TightDrop = 1, TightAlpha = 0.32;

    public readonly ShadowShape Soft, Tight;
    public readonly int Radius, SoftDy, TightDy;

    public ShadowLook(double scale)
    {
        Radius = (int)Math.Ceiling(Corner * scale);
        Soft = new ShadowShape(Radius, (int)Math.Ceiling(SoftSigma * scale));
        Tight = new ShadowShape(Radius, (int)Math.Ceiling(TightSigma * scale));
        SoftDy = (int)Math.Round(SoftDrop * scale);
        TightDy = Math.Max(1, (int)Math.Round(TightDrop * scale));
    }

    // How far past each edge of the window the shadow reaches.
    public int Side => Math.Max(Soft.Margin, Tight.Margin);
    public int Top => Math.Max(0, Math.Max(Soft.Margin - SoftDy, Tight.Margin - TightDy));
    public int Bottom => Math.Max(Soft.Margin + SoftDy, Tight.Margin + TightDy);

    // How dark the shadow of a w x h window is at (x, y) from its top-left corner; none where
    // the window itself is (so a window you can see through does not show it).
    public float At(double x, double y, double w, double h)
    {
        double a = SoftAlpha * Soft.At(x, y - SoftDy, w, h), b = TightAlpha * Tight.At(x, y - TightDy, w, h);
        return (float)((a + b * (1 - a)) * (1 - ShadowShape.Coverage(x, y, w, h, Radius)));
    }

    // The four pieces that hold the shadow of a window filling `frame`: above, below, left and
    // right of it. Under the window there is no need for any, except at its rounded corners,
    // so each piece reaches in over the window by the corners' radius. A piece with no room
    // (a window too small for sides) has no width or height.
    public PixelRect[] Pieces(PixelRect frame)
    {
        int l = frame.X - Side, r = frame.Right + Side, c = Radius;
        int sideTop = frame.Y + c, sideBottom = Math.Max(sideTop, frame.Bottom - c);
        return new[]
        {
            new PixelRect(l, frame.Y - Top, r - l, Math.Min(Top + c, frame.H + Top)),
            new PixelRect(l, Math.Max(frame.Y, frame.Bottom - c), r - l, Bottom + Math.Min(c, frame.H)),
            new PixelRect(l, sideTop, Side + c, sideBottom - sideTop),
            new PixelRect(frame.Right - c, sideTop, Side + c, sideBottom - sideTop),
        };
    }

    // Fills `pixels` (premultiplied BGRA, `stride` pixels a row) with the shadow of a window
    // filling `frame` as it falls inside `piece` (both on the screen): black, as dark as it is.
    public void Draw(Span<uint> pixels, int stride, PixelRect piece, PixelRect frame)
    {
        if (piece.W <= 0 || piece.H <= 0) return;
        int w = frame.W, h = frame.H;
        var softX = new int[piece.W]; var tightX = new int[piece.W]; var softY = new int[piece.H]; var tightY = new int[piece.H];
        for (int i = 0; i < piece.W; i++)
        {
            double x = piece.X + i + 0.5 - frame.X;
            softX[i] = Soft.Index(x, w);
            tightX[i] = Tight.Index(x, w);
        }
        for (int j = 0; j < piece.H; j++)
        {
            double y = piece.Y + j + 0.5 - frame.Y;
            softY[j] = Soft.Index(y - SoftDy, h);
            tightY[j] = Tight.Index(y - TightDy, h);
        }
        float[] sa = Soft.Alpha, ta = Tight.Alpha;
        int sn = Soft.Size, tn = Tight.Size;
        for (int j = 0; j < piece.H; j++)
        {
            double y = piece.Y + j + 0.5 - frame.Y;
            bool rows = y > -0.5 && y < h + 0.5;
            var row = pixels.Slice(j * stride, piece.W);
            for (int i = 0; i < piece.W; i++)
            {
                double a = softX[i] < 0 || softY[j] < 0 ? 0 : SoftAlpha * sa[softY[j] * sn + softX[i]];
                double b = tightX[i] < 0 || tightY[j] < 0 ? 0 : TightAlpha * ta[tightY[j] * tn + tightX[i]];
                double v = a + b * (1 - a);
                if (rows && v > 0)
                {
                    double x = piece.X + i + 0.5 - frame.X;
                    if (x > -0.5 && x < w + 0.5) v *= 1 - ShadowShape.Coverage(x, y, w, h, Radius);
                }
                row[i] = (uint)Math.Clamp(v * 255 + 0.5, 0, 255) << 24;
            }
        }
    }
}
