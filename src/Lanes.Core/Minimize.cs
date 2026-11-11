namespace Lanes.Core;

// A window minimizing into its taskbar button: it shrinks at once and travels as it does, faster
// as it goes, and fades only at the end, in the button. Coming back is the same run backwards.
public static class Minimize
{
    public const double Time = 0.35;

    // Where the window is drawn, and how opaque, `u` (0..1) of the way from `from` to `into`.
    public static (Box Box, double Alpha) At(Box from, Box into, double u)
    {
        u = Math.Clamp(u, 0, 1);
        double shrink = 1 - Math.Pow(1 - u, 3);
        double go = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(2 - 2 * u, 3) / 2;
        double cx = from.X + from.W / 2 + (into.X + into.W / 2 - from.X - from.W / 2) * go;
        double cy = from.Y + from.H / 2 + (into.Y + into.H / 2 - from.Y - from.H / 2) * go;
        double w = from.W + (into.W - from.W) * shrink, h = from.H + (into.H - from.H) * shrink;
        return (new Box(cx - w / 2, cy - h / 2, w, h), Math.Clamp((1 - u) / 0.3, 0, 1));
    }

    // A box of the window's shape, `width` wide, round the middle of its taskbar button.
    public static Box Into(Box window, double cx, double cy, double width)
    {
        double h = width * window.H / Math.Max(1, window.W);
        return new Box(cx - width / 2, cy - h / 2, width, h);
    }
}
