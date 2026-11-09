namespace Lanes.Core;

public enum TileKind { Desktop, Plus, NewLane, Trash }

// One box in the overview's lane rows: a desktop, the "+" (Lane is your lane, which it adds
// to), a slot for a new lane (Lane is where the new lane would be inserted), or the bin a
// dragged desktop is dropped in to close it.
public sealed record StripTile(TileKind Kind, int Lane, int Index, Guid Desktop, Box Rect);

// Where a dragged desktop would go: into a lane at an index (counted without the dragged
// desktop), or into a new lane inserted at Lane.
public readonly record struct DeskDrop(int Lane, int Index, bool NewLane);

// The lane rows at the top of the overview, one row per lane, top to bottom. Each row is
// shifted so that its current desktop (where a swipe up or down lands) sits in one column with
// the desktop you are on, in the middle: what is above and below you reads at a glance. Rows
// run off the screen's sides when long. One "+" in the top right corner adds a desktop at the
// end of your lane; a bin just below it (`bin`: there is more than one desktop) takes a
// desktop dropped on it and closes it. While something is dragged, a slot for a new lane opens
// above the rows and one below them.
public sealed class Strip
{
    public List<StripTile> Tiles { get; } = new();
    public Box Column { get; private set; }
    // Where the rows end, and where the window spread may start (room for a drag kept free).
    public double Bottom { get; private set; }
    public double SpreadTop { get; private set; }
    public double TileW { get; private set; }
    public double TileH { get; private set; }
    public double Gap { get; private set; }
    public double RowGap { get; private set; }
    public double Caption { get; private set; }
    readonly List<double> _rowY = new();

    public double RowY(int lane) => _rowY[lane];

    // Tiles have the screen's shape (`width` x `height`); the rows start `offset` down from its
    // top and take about a third of the `room` below that (by default the whole height), as the
    // Spaces bar does in Mission Control, leaving the rest to the windows.
    public static Strip Layout(LaneMap map, Guid current, double width, double height, double s, bool dragging = false, double offset = 0, double? room = null, bool bin = false)
    {
        var strip = new Strip();
        int n = map.Lanes.Count;
        double gap = 16 * s, rowGap = 14 * s, pad = 18 * s, top = offset + pad, slotH = 32 * s, caption = 20 * s, plus = 32 * s;
        double tw = 160 * s, aspect = height / width, limit = (room ?? height) * 0.36;
        // The rows and, while dragging, the two slots.
        double Height(double w) => n * (w * aspect + caption) + (n - 1) * rowGap + 2 * (slotH + rowGap);
        while (tw > 96 * s && pad + Height(tw) > limit) tw -= 4 * s;
        double th = tw * aspect, rowH = th + caption;
        double column = (width - tw) / 2;
        strip.TileW = tw; strip.TileH = th; strip.Gap = gap; strip.RowGap = rowGap; strip.Caption = caption;
        double y0 = top;
        if (dragging)
        {
            strip.Tiles.Add(new StripTile(TileKind.NewLane, 0, 0, Guid.Empty, new Box(column, top, tw, slotH)));
            y0 += slotH + rowGap;
        }
        for (int l = 0; l < n; l++)
        {
            var lane = map.Lanes[l];
            Guid anchor = lane.Desktops.Contains(current) ? current : map.CurrentOf(l) ?? Guid.Empty;
            int c = Math.Max(0, lane.Desktops.IndexOf(anchor));
            double y = y0 + l * (rowH + rowGap), x = column - c * (tw + gap);
            strip._rowY.Add(y);
            for (int i = 0; i < lane.Desktops.Count; i++, x += tw + gap)
                strip.Tiles.Add(new StripTile(TileKind.Desktop, l, i, lane.Desktops[i], new Box(x, y, tw, th)));
        }
        double rowsEnd = y0 + n * (rowH + rowGap) - rowGap;
        if (dragging) strip.Tiles.Add(new StripTile(TileKind.NewLane, n, 0, Guid.Empty, new Box(column, rowsEnd + rowGap, tw, slotH)));
        strip.Tiles.Add(new StripTile(TileKind.Plus, map.Locate(current)?.Lane ?? 0, 0, Guid.Empty, new Box(width - 28 * s - plus, top, plus, plus)));
        if (bin) strip.Tiles.Add(new StripTile(TileKind.Trash, 0, 0, Guid.Empty, new Box(width - 28 * s - plus, top + plus + 12 * s, plus, plus)));
        strip.Bottom = (dragging ? rowsEnd + rowGap + slotH : rowsEnd) + pad;
        strip.Column = new Box(column - 16 * s, y0 - 8 * s, tw + 32 * s, rowsEnd - y0 + 16 * s);
        // The spread starts below where the rows reach while dragging, so a drag does not move it.
        double dragEnd = top + n * (rowH + rowGap) - rowGap + 2 * (slotH + rowGap);
        strip.SpreadTop = dragEnd + 56 * s;
        return strip;
    }

    // The lane whose row is at height y, if any.
    public int? LaneAt(double y)
    {
        for (int l = 0; l < _rowY.Count; l++)
            if (y >= _rowY[l] - RowGap / 2 && y < _rowY[l] + TileH + Caption + RowGap / 2) return l;
        return null;
    }

    public StripTile? Hit(double x, double y) =>
        Tiles.FirstOrDefault(t => x >= t.Rect.X && x < t.Rect.Right && y >= t.Rect.Y && y < t.Rect.Bottom);

    // Where a desktop dragged to (x, y) would go; null when it would stay where it is or the
    // point is not over the rows.
    public DeskDrop? DropFor(double x, double y, Guid dragged, LaneMap map)
    {
        foreach (var t in Tiles.Where(t => t.Kind == TileKind.NewLane))
            if (x >= t.Rect.X - Gap && x < t.Rect.Right + Gap && y >= t.Rect.Y - RowGap / 2 && y < t.Rect.Bottom + RowGap / 2)
            {
                // A desktop alone in its lane moved into a new lane next to it would not change anything.
                if (map.Locate(dragged) is var (l, _) && map.Lanes[l].Desktops.Count == 1 && (t.Lane == l || t.Lane == l + 1)) return null;
                return new DeskDrop(t.Lane, 0, true);
            }
        for (int lane = 0; lane < _rowY.Count; lane++)
        {
            double top = _rowY[lane] - RowGap / 2, bottom = _rowY[lane] + TileH + Caption + RowGap / 2;
            if (y < top || y >= bottom) continue;
            int index = Tiles.Count(t => t.Kind == TileKind.Desktop && t.Lane == lane && t.Desktop != dragged && t.Rect.X + t.Rect.W / 2 < x);
            if (map.Locate(dragged) is var (dl, di) && dl == lane && di == index) return null;
            return new DeskDrop(lane, index, false);
        }
        return null;
    }

    // The bar that shows where a dragged desktop would be inserted in a row.
    public Box Marker(DeskDrop drop, Guid dragged, double s)
    {
        var row = Tiles.Where(t => t.Kind == TileKind.Desktop && t.Lane == drop.Lane && t.Desktop != dragged).ToList();
        double y = _rowY[drop.Lane], x;
        if (row.Count == 0) x = Column.X + 16 * s - Gap / 2;
        else if (drop.Index <= 0) x = row[0].Rect.X - Gap / 2;
        else if (drop.Index >= row.Count) x = row[^1].Rect.Right + Gap / 2;
        else x = (row[drop.Index - 1].Rect.Right + row[drop.Index].Rect.X) / 2;
        return new Box(x - 2 * s, y - 4 * s, 4 * s, TileH + 8 * s);
    }
}
