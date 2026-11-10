namespace Lanes.Core;

public enum TileKind { Desktop, Plus, NewLane, Trash, NewDesk }

// One box in the overview's lane rows: a desktop, the "+" (Lane is your lane, which it adds
// to), a slot for a new lane (Lane is where the new lane would be inserted), a slot for a new
// desktop at one end of a lane (Index is where in the lane it would go), or the bin a dragged
// desktop is dropped in to close it.
public sealed record StripTile(TileKind Kind, int Lane, int Index, Guid Desktop, Box Rect);

// Where a dragged desktop would go: into a lane at an index (counted without the dragged
// desktop), or into a new lane inserted at Lane.
public readonly record struct DeskDrop(int Lane, int Index, bool NewLane);

// The lane rows at the top of the overview, one row per lane, top to bottom. Each row is
// shifted so that its current desktop (where a swipe up or down lands) sits in one column with
// the desktop you are on, in the middle: what is above and below you reads at a glance. Rows
// run off the screen's sides when long. One "+" in the top right corner adds a desktop at the
// end of your lane; a bin just below it (`bin`: there is more than one desktop) takes a
// desktop dropped on it and closes it. While something is dragged, the room above the rows and
// the room below them take it into a new lane there, and the room at each end of a row to a
// new desktop there: thin bars (Bar) show those places, and nothing moves for them. The place
// the dragged thing is held over (`open`) opens: the rows below a new lane move down a row and
// the band grows; in a row, the desktops past the place, on the side away from the middle
// column, move a step out. A block (Opened) then stands where the new lane's or desktop's
// first desktop will be.
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
    // The place opened, where the new desktop would be, and its slot (the room it takes for
    // what is held over it): a NewLane tile, or a NewDesk tile whose Index is where in the lane.
    public Box? Opened { get; private set; }
    public StripTile? OpenedSlot { get; private set; }
    readonly List<double> _rowY = new();
    double _column, _pad;

    public double RowY(int lane) => _rowY[lane];

    // Tiles have the screen's shape (`width` x `height`); the rows start `offset` down from its
    // top and take about a third of the `room` below that (by default the whole height), as the
    // Spaces bar does in Mission Control, leaving the rest to the windows. `open` is counted as
    // DropFor counts it, without the `dragged` desktop.
    public static Strip Layout(LaneMap map, Guid current, double width, double height, double s, bool dragging = false,
        double offset = 0, double? room = null, bool bin = false, DeskDrop? open = null, Guid dragged = default)
    {
        var strip = new Strip();
        int n = map.Lanes.Count;
        double gap = 16 * s, rowGap = 14 * s, pad = 18 * s, top = offset + pad, slot = 32 * s, caption = 20 * s, plus = 32 * s;
        double tw = 160 * s, aspect = height / width, limit = (room ?? height) * 0.36;
        double Height(double w) => n * (w * aspect + caption) + (n - 1) * rowGap;
        while (tw > 96 * s && pad + Height(tw) > limit) tw -= 4 * s;
        double th = tw * aspect, rowH = th + caption, step = tw + gap, rowStep = rowH + rowGap;
        double column = (width - tw) / 2;
        strip.TileW = tw; strip.TileH = th; strip.Gap = gap; strip.RowGap = rowGap; strip.Caption = caption;
        strip._column = column;
        strip._pad = pad;
        double y0 = top;
        int newLane = dragging && open is { NewLane: true } nl ? Math.Clamp(nl.Lane, 0, n) : -1;
        double RowTop(int l) => y0 + (l + (newLane >= 0 && l >= newLane ? 1 : 0)) * rowStep;
        double rowsEnd = y0 + (n + (newLane >= 0 ? 1 : 0)) * rowStep - rowGap;
        if (newLane >= 0)
        {
            // The new lane's row, and the room it takes: the whole row, and for one at an edge
            // of the rows the room past it that opened it.
            double y = y0 + newLane * rowStep;
            double from = newLane == 0 ? 0 : newLane == n ? y - rowGap - caption : y - rowGap / 2;
            double to = y + rowH + (newLane == n ? pad : rowGap / 2);
            strip.Opened = new Box(column, y, tw, th);
            strip.OpenedSlot = new StripTile(TileKind.NewLane, newLane, 0, Guid.Empty, new Box(0, from, width, to - from));
        }
        // Above the rows, up to the top of the screen.
        if (dragging) strip.Tiles.Add(newLane == 0 ? strip.OpenedSlot! : new StripTile(TileKind.NewLane, 0, 0, Guid.Empty, new Box(0, 0, width, top)));
        for (int l = 0; l < n; l++)
        {
            var lane = map.Lanes[l];
            Guid anchor = lane.Desktops.Contains(current) ? current : map.CurrentOf(l) ?? Guid.Empty;
            int c = Math.Max(0, lane.Desktops.IndexOf(anchor)), k = lane.Desktops.Count;
            double y = RowTop(l), x0 = column - c * step;
            strip._rowY.Add(y);
            // A place opened in this row, before the desktop at `at` (counting the dragged one).
            int at = -1;
            if (dragging && open is { NewLane: false } o && o.Lane == l)
            {
                int p = lane.Desktops.IndexOf(dragged);
                at = Math.Clamp(p >= 0 && o.Index >= p ? o.Index + 1 : o.Index, 0, k);
            }
            double X(int i) => x0 + i * step + (at < 0 ? 0 : at > c ? (i >= at ? step : 0) : (i < at ? -step : 0));
            if (at >= 0)
            {
                // Its room: the block and the gaps beside it, and the edge of the desktop next to
                // the gap it opened from, so it stays open while held where it opened.
                double gx = x0 + (at > c ? at : at - 1) * step, from = x0 + at * step - gap / 2;
                double left = Math.Min(gx - gap / 2, from - Near(gap, tw)), right = Math.Max(gx + tw + gap / 2, from + Near(gap, tw));
                strip.Opened = new Box(gx, y, tw, th);
                strip.OpenedSlot = new StripTile(TileKind.NewDesk, l, at, Guid.Empty, new Box(left, y, right - left, th));
                strip.Tiles.Add(strip.OpenedSlot);
            }
            if (dragging && at != 0) strip.Tiles.Add(new StripTile(TileKind.NewDesk, l, 0, Guid.Empty, new Box(X(0) - gap - slot, y, slot, th)));
            for (int i = 0; i < k; i++)
                strip.Tiles.Add(new StripTile(TileKind.Desktop, l, i, lane.Desktops[i], new Box(X(i), y, tw, th)));
            if (dragging && at != k) strip.Tiles.Add(new StripTile(TileKind.NewDesk, l, k, Guid.Empty, new Box(X(k - 1) + step, y, slot, th)));
        }
        // Below the last row's desktops, past their names to the band's edge.
        if (dragging) strip.Tiles.Add(newLane == n ? strip.OpenedSlot! : new StripTile(TileKind.NewLane, n, 0, Guid.Empty, new Box(0, rowsEnd - caption, width, caption + pad)));
        strip.Tiles.Add(new StripTile(TileKind.Plus, map.Locate(current)?.Lane ?? 0, 0, Guid.Empty, new Box(width - 28 * s - plus, top, plus, plus)));
        if (bin) strip.Tiles.Add(new StripTile(TileKind.Trash, 0, 0, Guid.Empty, new Box(width - 28 * s - plus, top + plus + 12 * s, plus, plus)));
        strip.Bottom = rowsEnd + pad;
        strip.Column = new Box(column - 16 * s, y0 - 8 * s, tw + 32 * s, rowsEnd - y0 + 16 * s);
        // Room under the band for a window dragged up to the rows to shrink in.
        strip.SpreadTop = strip.Bottom + 56 * s;
        return strip;
    }

    // Where a slot shows, `thick` across: a new lane as a bar along the middle column in the room
    // above or below the rows, a new desktop as one upright in the gap at a row's end.
    public Box Bar(StripTile t, double thick) => t.Kind switch
    {
        TileKind.NewLane => new Box(_column, t.Rect.Bottom - _pad / 2 - thick / 2, TileW, thick),
        TileKind.NewDesk => new Box((t.Index == 0 ? t.Rect.Right + Gap / 2 : t.Rect.X - Gap / 2) - thick / 2, t.Rect.Y, thick, t.Rect.H),
        _ => t.Rect,
    };

    // Where the block of a place grows out of before it opens (as DropFor counts, without the
    // `dragged` desktop): its bar, or one in the gap between two desktops.
    public Box Seed(DeskDrop place, Guid dragged, double thick)
    {
        if (place.NewLane)
            return Tiles.FirstOrDefault(t => t.Kind == TileKind.NewLane && t.Lane == place.Lane) is { } lane ? Bar(lane, thick) : new Box(_column, Bottom, TileW, thick);
        var row = Tiles.Where(t => t.Kind == TileKind.Desktop && t.Lane == place.Lane).ToList();
        int p = row.FindIndex(t => t.Desktop == dragged), at = Math.Clamp(p >= 0 && place.Index >= p ? place.Index + 1 : place.Index, 0, row.Count);
        double y = _rowY[place.Lane], x = row.Count == 0 ? _column + TileW / 2
            : at == 0 ? row[0].Rect.X - Gap / 2 : at == row.Count ? row[^1].Rect.Right + Gap / 2 : (row[at - 1].Rect.Right + row[at].Rect.X) / 2;
        return new Box(x - thick / 2, y, thick, TileH);
    }

    // Held this near the gap between two desktops of a row (and not at a row's end), a window
    // asks for a new desktop there: the place, counted in the lane as it is.
    public DeskDrop? Between(double x, double y)
    {
        for (int lane = 0; lane < _rowY.Count; lane++)
        {
            if (y < _rowY[lane] || y >= _rowY[lane] + TileH) continue;
            var row = Tiles.Where(t => t.Kind == TileKind.Desktop && t.Lane == lane).OrderBy(t => t.Rect.X).ToList();
            for (int i = 1; i < row.Count; i++)
                if (Math.Abs(x - (row[i - 1].Rect.Right + row[i].Rect.X) / 2) <= Near(Gap, TileW) && row[i].Rect.X - row[i - 1].Rect.Right < 2 * Gap)
                    return new DeskDrop(lane, row[i].Index, false);
        }
        return null;
    }

    // How near a gap's middle counts as by it: the gap and a little of each desktop beside it.
    static double Near(double gap, double tileW) => gap / 2 + 0.15 * tileW;

    // The lane whose row is at height y, if any.
    public int? LaneAt(double y)
    {
        for (int l = 0; l < _rowY.Count; l++)
            if (y >= _rowY[l] - RowGap / 2 && y < _rowY[l] + TileH + Caption + RowGap / 2) return l;
        return null;
    }

    // The "+" and the bin first: a new lane's room can reach their corner.
    public StripTile? Hit(double x, double y) =>
        Tiles.OrderBy(t => t.Kind is TileKind.Plus or TileKind.Trash ? 0 : 1).FirstOrDefault(t => x >= t.Rect.X && x < t.Rect.Right && y >= t.Rect.Y && y < t.Rect.Bottom);

    // Where a desktop dragged to (x, y) would go; null when it would stay where it is or the
    // point is not over the rows.
    public DeskDrop? DropFor(double x, double y, Guid dragged, LaneMap map)
    {
        foreach (var t in Tiles.Where(t => t.Kind == TileKind.NewLane))
            if (x >= t.Rect.X && x < t.Rect.Right && y >= t.Rect.Y && y < t.Rect.Bottom)
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
}
