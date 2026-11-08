namespace Lanes.Core;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public Box Lerp(Box to, double t) => new(X + (to.X - X) * t, Y + (to.Y - Y) * t, W + (to.W - W) * t, H + (to.H - H) * t);
}

// Lays the windows of a desktop out for the overview: one scale for all of them (so the
// biggest stays the biggest), rows filled in the windows' own top-to-bottom, left-to-right
// order, the largest scale that fits the area, and the group centred.
public static class Spread
{
    public static Box[] Layout(IReadOnlyList<Box> windows, Box area, double gapX = 32, double gapY = 48, double minWidth = 200)
    {
        int n = windows.Count;
        var result = new Box[n];
        if (n == 0) return result;
        var order = Enumerable.Range(0, n).OrderBy(i => windows[i].Y + windows[i].H / 2).ThenBy(i => windows[i].X).ToArray();

        for (double s = 1; s > 0.02; s -= 0.005)
        {
            double Width(int i) => Math.Min(area.W, Math.Max(windows[i].W * s, Math.Min(minWidth, windows[i].W)));
            double Height(int i) => windows[i].H * Width(i) / windows[i].W;

            var rows = new List<List<int>> { new() };
            double rowWidth = 0;
            foreach (int i in order)
            {
                double w = Width(i);
                if (rows[^1].Count > 0 && rowWidth + gapX + w > area.W)
                {
                    rows.Add(new List<int>());
                    rowWidth = 0;
                }
                rowWidth += (rows[^1].Count > 0 ? gapX : 0) + w;
                rows[^1].Add(i);
            }
            var heights = rows.Select(r => r.Max(Height)).ToList();
            double total = heights.Sum() + gapY * (rows.Count - 1);
            if (total > area.H && s > 0.025) continue;

            double y = area.Y + Math.Max(0, (area.H - total) / 2);
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r].OrderBy(i => windows[i].X).ToList();
                double width = row.Sum(Width) + gapX * (row.Count - 1);
                double x = area.X + (area.W - width) / 2;
                foreach (int i in row)
                {
                    double w = Width(i), h = Height(i);
                    result[i] = new Box(x, y, w, h); // top-aligned: labels above share a line
                    x += w + gapX;
                }
                y += heights[r] + gapY;
            }
            return result;
        }
        return result;
    }
}
