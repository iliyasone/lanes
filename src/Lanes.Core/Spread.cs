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
// is tried and the one that leaves the windows biggest wins. One scale for all, so the biggest
// stays the biggest, and never above their real size. `gap` is kept free between windows.
public static class Spread
{
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
        foreach (var row in chosen)
        {
            row.Sort((a, b) => (windows[a].X + windows[a].W / 2).CompareTo(windows[b].X + windows[b].W / 2));
            double rowH = row.Max(i => windows[i].H) * s;
            double x = area.X + (area.W - (row.Sum(i => windows[i].W) * s + gap * (row.Count - 1))) / 2;
            foreach (int i in row)
            {
                double w = windows[i].W * s, h = windows[i].H * s;
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
