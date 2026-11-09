namespace Lanes.Core;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public Box Lerp(Box to, double t) => new(X + (to.X - X) * t, Y + (to.Y - Y) * t, W + (to.W - W) * t, H + (to.H - H) * t);
}

// Lays the windows of a desktop out for the overview as Mission Control does: each window
// starts where it is on the screen, windows that overlap are pushed apart (each away from the
// other's centre) until none do, and the group is scaled down only as much as it needs to fit
// the area, never up, and centred. Windows that already sit apart keep their places and their
// size; one window alone shrinks only to fit. One scale for all, so the biggest stays the
// biggest. Each window keeps `gap` free at its sides and `above` free over it for its name.
public static class Spread
{
    public static Box[] Layout(IReadOnlyList<Box> windows, Box area, double gap = 32, double above = 52)
    {
        int n = windows.Count;
        var result = new Box[n];
        if (n == 0) return result;
        // The room around each window is meant on the screen, after scaling, and the scale is
        // only known at the end: a few rounds settle on it.
        double scale = Math.Min(1, Fit(Union(windows), area));
        var rooms = new Box[n];
        for (int round = 0; round < 4; round++)
        {
            double side = gap / 2 / scale, top = above / scale;
            for (int i = 0; i < n; i++)
                rooms[i] = new Box(windows[i].X - side, windows[i].Y - top, windows[i].W + 2 * side, windows[i].H + top);
            Separate(rooms, area.H / area.W);
            double s = Math.Min(1, Fit(Union(rooms), area));
            bool settled = Math.Abs(s - scale) < 0.005 * scale;
            scale = s;
            if (settled) break;
        }
        var bounds = Union(rooms);
        double bx = bounds.X + bounds.W / 2, by = bounds.Y + bounds.H / 2, ax = area.X + area.W / 2, ay = area.Y + area.H / 2;
        for (int i = 0; i < n; i++)
        {
            // Where the window lies in its room, kept in screen pixels as the room was made.
            double wx = rooms[i].X + (rooms[i].W - windows[i].W) / 2, wy = rooms[i].Bottom - windows[i].H;
            result[i] = new Box(ax + (wx - bx) * scale, ay + (wy - by) * scale, windows[i].W * scale, windows[i].H * scale);
        }
        return result;
    }

    static double Fit(Box b, Box area) => Math.Min(area.W / Math.Max(1, b.W), area.H / Math.Max(1, b.H));

    static Box Union(IEnumerable<Box> boxes)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var x in boxes) { l = Math.Min(l, x.X); t = Math.Min(t, x.Y); r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom); }
        return new Box(l, t, r - l, b - t);
    }

    static bool Overlap(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    // Pushes overlapping boxes apart in small steps, each pair along the line between their
    // centres, until no two overlap. The group is kept close to the area's shape (`aspect`,
    // height over width) by pushing less along the side it is already too long in.
    static void Separate(Box[] r, double aspect)
    {
        int n = r.Length;
        double step = Math.Max(4, r.Max(b => Math.Max(b.W, b.H)) / 60);
        for (int pass = 0; pass < 10000; pass++)
        {
            bool overlap = false;
            var bounds = Union(r);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (!Overlap(r[i], r[j])) continue;
                    overlap = true;
                    double dx = r[j].X + r[j].W / 2 - (r[i].X + r[i].W / 2), dy = r[j].Bottom - r[j].H / 2 - (r[i].Bottom - r[i].H / 2);
                    if (Math.Abs(dx) + Math.Abs(dy) < 1e-6)
                    {
                        // Same centre (two maximized windows): each pair its own direction, sideways first.
                        double angle = Math.PI * (1 + 2.0 * (i + j - 1) / Math.Max(2, n));
                        (dx, dy) = (-Math.Cos(angle), Math.Sin(angle) * 0.5);
                    }
                    if (bounds.H / bounds.W > aspect) dy /= 2; else dx /= 2;
                    double len = Math.Abs(dx) + Math.Abs(dy);
                    if (len < 1e-9) { dx = 1; len = 1; }
                    dx *= step / len; dy *= step / len;
                    r[i] = r[i] with { X = r[i].X - dx, Y = r[i].Y - dy };
                    r[j] = r[j] with { X = r[j].X + dx, Y = r[j].Y + dy };
                }
            if (!overlap) return;
        }
    }
}
