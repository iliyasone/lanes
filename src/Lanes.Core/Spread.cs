namespace Lanes.Core;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public Box Lerp(Box to, double t) => new(X + (to.X - X) * t, Y + (to.Y - Y) * t, W + (to.W - W) * t, H + (to.H - H) * t);
}

// Lays the windows of a desktop out for the overview as large as the area allows, in rows, none
// over another: windows higher on the screen go in the upper rows, and each row keeps their
// order from left to right, so every window is about where you left it. Every number of rows
// is tried and the one that leaves the windows biggest wins: the biggest window takes the scale
// that allows. Smaller ones shrink less, a small one beside a big one not at all where its row
// has room, so the biggest still looks the biggest; none goes above its real size or out of its
// row. `gap` is kept free between windows.
public static class Spread
{
    // Two windows change places, each keeping its size: each goes where the other was, against
    // the edges of the screen the other touched (two windows side by side change sides and stay
    // against the screen's edges), else centred on where the other was, and stays on the screen
    // (`work`). `near`: how close to an edge counts as touching it.
    public static (Box A, Box B) Trade(Box a, Box b, Box work, double near = 8) =>
        (Into(a, b, work, near), Into(b, a, work, near));

    static Box Into(Box w, Box place, Box work, double near) =>
        new(Along(w.W, place.X, place.Right, work.X, work.Right, near), Along(w.H, place.Y, place.Bottom, work.Y, work.Bottom, near), w.W, w.H);

    static double Along(double size, double from, double to, double min, double max, double near)
    {
        double at = from <= min + near ? from : to >= max - near ? to - size : (from + to - size) / 2;
        return size >= max - min ? min : Math.Clamp(at, min, max - size);
    }

    public static Box[] Layout(IReadOnlyList<Box> windows, Box area, double gap = 32)
    {
        int n = windows.Count;
        var result = new Box[n];
        if (n == 0) return result;
        var byHeight = Enumerable.Range(0, n).OrderBy(i => windows[i].Y + windows[i].H / 2).ThenBy(i => windows[i].X).ToList();
        double totalW = windows.Sum(w => w.W);
        List<List<int>>? best = null;
        double bestScale = 0;
        for (int count = 1; count <= n; count++)
        {
            var rows = Rows(byHeight, windows, totalW / count, count);
            if (rows.Count != count) continue;
            double scale = Scale(rows, windows, area, gap);
            if (scale > bestScale * 1.0001) { best = rows; bestScale = scale; }
        }
        double s = Math.Min(1, bestScale);
        var chosen = best!;
        double height = chosen.Sum(r => r.Max(i => windows[i].H)) * s + gap * (chosen.Count - 1);
        double y = area.Y + (area.H - height) / 2;
        double most = windows.Max(w => w.W * w.H);
        var k = new double[n];
        foreach (var row in chosen)
        {
            row.Sort((a, b) => (windows[a].X + windows[a].W / 2).CompareTo(windows[b].X + windows[b].W / 2));
            double rowH = row.Max(i => windows[i].H) * s;
            // A window a quarter the size (in area) of the biggest shrinks half as much, and so on;
            // never taller than its row.
            foreach (int i in row)
                k[i] = Math.Max(s, Math.Min(Math.Min(1, s * Math.Pow(most / Math.Max(1, windows[i].W * windows[i].H), 0.25)), rowH / windows[i].H));
            // Back toward the one scale as far as the row needs to fit across.
            double room = area.W - gap * (row.Count - 1) - row.Sum(i => windows[i].W * s), more = row.Sum(i => windows[i].W * (k[i] - s));
            if (more > room)
                foreach (int i in row) k[i] = s + (k[i] - s) * Math.Max(0, room) / more;
            double x = area.X + (area.W - (row.Sum(i => windows[i].W * k[i]) + gap * (row.Count - 1))) / 2;
            foreach (int i in row)
            {
                double w = windows[i].W * k[i], h = windows[i].H * k[i];
                result[i] = new Box(x, y + (rowH - h) / 2, w, h);
                x += w + gap;
            }
            y += rowH + gap;
        }
        return result;
    }

    // The windows, top to bottom, split into `count` rows of about `ideal` width each: a window
    // joins the row while that brings the row nearer the ideal.
    static List<List<int>> Rows(List<int> order, IReadOnlyList<Box> windows, double ideal, int count)
    {
        var rows = new List<List<int>>();
        int k = 0;
        for (int r = 0; r < count && k < order.Count; r++)
        {
            var row = new List<int>();
            double width = 0;
            for (; k < order.Count; k++)
            {
                double w = windows[order[k]].W;
                bool last = r == count - 1;
                if (!last && row.Count > 0 && width + w > ideal && Math.Abs(1 - (width + w) / ideal) >= Math.Abs(1 - width / ideal)) break;
                row.Add(order[k]);
                width += w;
            }
            rows.Add(row);
        }
        return rows;
    }

    // The largest scale at which these rows fit the area with `gap` between windows and rows.
    static double Scale(List<List<int>> rows, IReadOnlyList<Box> windows, Box area, double gap)
    {
        double sumH = rows.Sum(r => r.Max(i => windows[i].H));
        double s = (area.H - gap * (rows.Count - 1)) / Math.Max(1, sumH);
        foreach (var row in rows)
            s = Math.Min(s, (area.W - gap * (row.Count - 1)) / Math.Max(1, row.Sum(i => windows[i].W)));
        return Math.Max(0.01, s);
    }
}
